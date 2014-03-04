﻿namespace LinqOptimizer.Gpu
    open System
    open System.Linq.Expressions
    open System.Runtime.InteropServices
    open LinqOptimizer.Core
    open OpenCL.Net

    module internal Compiler =
        type Length = int
        type Size = int

        type QueryContext = { CurrentVarExpr : ParameterExpression; AccVarExpr : ParameterExpression; FlagVarExpr : ParameterExpression;
                                BreakLabel : LabelTarget; ContinueLabel : LabelTarget;
                                InitExprs : Expression list; AccExpr : Expression; CombinerExpr : Expression; ResultType : Type; 
                                VarExprs : ParameterExpression list; Exprs : Expression list; ReductionType : ReductionType }

        type CompilerResult = { Source : string; ReductionType : ReductionType; Args : (IGpuArray * Type * Length * Size) [] }
        
        let intType = typeof<int>
        let floatType = typeof<single>
        let doubleType = typeof<double>
        let byteType = typeof<byte>
        let gpuArrayTypeDef = typedefof<GpuArray<_>>

        let breakLabel () = labelTarget "brk"
        let continueLabel () = labelTarget "cont"

        let rec compile (queryExpr : QueryExpr) : CompilerResult = 

            let rec compile' (queryExpr : QueryExpr) (context : QueryContext) =
                let typeToStr (t : Type) = 
                    match t with
                    | TypeCheck intType _ -> "int"
                    | TypeCheck floatType _ -> "float"
                    | TypeCheck doubleType _ -> "float"
                    | TypeCheck byteType _ -> "byte"
                    | _ -> failwithf "Not supported %A" t
                let varExprToStr (varExpr : ParameterExpression) (vars : ParameterExpression list) = 
                    let index = vars |> List.findIndex (fun varExpr' -> varExpr = varExpr')
                    sprintf "%s%d" (varExpr.ToString()) index

                let rec exprToStr (expr : Expression) (vars : ParameterExpression list) =
                    match expr with
                    | Constant (value, TypeCheck intType _) -> sprintf "%A" value
                    | Constant (value, TypeCheck floatType _) -> sprintf "%A" value
                    | Constant (value, TypeCheck doubleType _) -> sprintf "%A" value
                    | Constant (value, TypeCheck byteType _) -> sprintf "%A" value
                    | Parameter (paramExpr) -> varExprToStr paramExpr vars
                    | Assign (Parameter (paramExpr), expr') -> sprintf "%s = %s" (varExprToStr paramExpr vars) (exprToStr expr' vars)
                    | Plus (leftExpr, rightExpr) -> sprintf "(%s + %s)" (exprToStr leftExpr vars) (exprToStr rightExpr vars)
                    | Times (leftExpr, rightExpr) -> sprintf "(%s * %s)" (exprToStr leftExpr vars) (exprToStr rightExpr vars)
                    | Modulo (leftExpr, rightExpr) -> sprintf "(%s %% %s)" (exprToStr leftExpr vars) (exprToStr rightExpr vars)
                    | Equal (leftExpr, rightExpr) -> sprintf "(%s == %s)" (exprToStr leftExpr vars) (exprToStr rightExpr vars)
                    | IFThenElse (testExpr, thenExpr, elseExpr) -> 
                        sprintf "if (%s) { %s; } else { %s; }" (exprToStr testExpr vars) (exprToStr thenExpr vars) (exprToStr elseExpr vars)
                    | Goto (kind, target, value) when kind = GotoExpressionKind.Continue -> sprintf "goto %s" target.Name 
                    | Block (_, exprs, _) -> 
                        exprs
                            |> Seq.map (fun expr -> sprintf "%s" (exprToStr expr vars))
                            |> Seq.reduce (fun first second -> sprintf "%s;%s%s" first Environment.NewLine second)
                    | Convert (expr, t) -> sprintf "((%s) %s)" (typeToStr t) (exprToStr expr vars)
                    | Nop _ -> ""
                    | _ -> failwithf "Not supported %A" expr


                match queryExpr with
                | Source (Constant (value, Named (TypeCheck gpuArrayTypeDef _, [|_|])) as expr, sourceType, QueryExprType.Gpu) ->
                    let vars = context.VarExprs
                    let sourceTypeStr = typeToStr sourceType
                    let resultTypeStr = typeToStr context.ResultType
                    let gpuArraySource = value :?> IGpuArray
                    let sourceLength = gpuArraySource.Length
                    let varsStr = vars 
                                      |> List.map (fun varExpr -> sprintf "%s %s;" (typeToStr varExpr.Type) (varExprToStr varExpr vars)) 
                                      |> List.reduce (fun first second -> sprintf "%s%s%s" first Environment.NewLine second)
                    let exprsStr = context.Exprs
                                       |> List.map (fun expr -> sprintf "%s;" (exprToStr expr vars))
                                       |> List.reduce (fun first second -> sprintf "%s%s%s" first Environment.NewLine second)
                    match context.ReductionType with
                    | ReductionType.Map ->
                        let source = KernelTemplates.mapTemplate sourceTypeStr resultTypeStr varsStr (varExprToStr context.CurrentVarExpr vars) exprsStr (varExprToStr context.AccVarExpr vars)
                        { Source = source; ReductionType = context.ReductionType; Args = [| (gpuArraySource, sourceType, sourceLength, Marshal.SizeOf(sourceType)) |] }
                    | ReductionType.Filter ->
                        let source = KernelTemplates.mapFilterTemplate sourceTypeStr resultTypeStr varsStr (varExprToStr context.CurrentVarExpr vars) exprsStr (varExprToStr context.FlagVarExpr vars) (varExprToStr context.AccVarExpr vars) 
                        { Source = source; ReductionType = context.ReductionType; Args = [| (value :?> IGpuArray, sourceType, sourceLength, Marshal.SizeOf(sourceType)) |] }
                    | ReductionType.Sum | ReductionType.Count -> 
                        let gpuArray = value :?> IGpuArray
                        let source = KernelTemplates.reduceTemplate sourceTypeStr resultTypeStr resultTypeStr varsStr (varExprToStr context.CurrentVarExpr vars) exprsStr (varExprToStr context.AccVarExpr vars) "0" "+"
                        { Source = source; ReductionType = context.ReductionType; Args = [| (gpuArray, sourceType, sourceLength, Marshal.SizeOf(sourceType)) |] }
                    | _ -> failwithf "Not supported %A" context.ReductionType
                | ZipWith ((Constant (first, Named (TypeCheck gpuArrayTypeDef _, [|_|])) as firstExpr), 
                            (Constant (second, Named (TypeCheck gpuArrayTypeDef _, [|_|])) as secondExpr), Lambda ([firstParamExpr; secondParamExpr], bodyExpr)) ->
                    let vars = context.VarExprs @ [firstParamExpr; secondParamExpr]
                    let resultTypeStr = typeToStr context.ResultType
                    let firstGpuArray = first :?> IGpuArray
                    let secondGpuArray = second :?> IGpuArray
                    let sourceLength = firstGpuArray.Length
                    let varsStr = vars 
                                      |> List.map (fun varExpr -> sprintf "%s %s;" (typeToStr varExpr.Type) (varExprToStr varExpr vars)) 
                                      |> List.fold (fun first second -> sprintf "%s%s%s" first Environment.NewLine second) ""
                    let exprsStr = context.Exprs
                                       |> List.map (fun expr -> sprintf "%s;" (exprToStr expr vars))
                                       |> List.fold (fun first second -> sprintf "%s%s%s" first Environment.NewLine second) ""
                    match context.ReductionType with
                    | ReductionType.Map ->
                        let source = KernelTemplates.zip2MapTemplate (typeToStr firstGpuArray.Type) (typeToStr secondGpuArray.Type) resultTypeStr 
                                                                     varsStr (varExprToStr firstParamExpr vars) (varExprToStr secondParamExpr vars) 
                                                                     (varExprToStr context.CurrentVarExpr vars) (exprToStr bodyExpr vars)
                                                                     exprsStr (varExprToStr context.AccVarExpr vars)
                        { Source = source; ReductionType = context.ReductionType; Args = [| (firstGpuArray, firstGpuArray.Type, firstGpuArray.Length, firstGpuArray.Size); 
                                                                                            (secondGpuArray, secondGpuArray.Type, secondGpuArray.Length, secondGpuArray.Size) |] }
                    | ReductionType.Filter ->
                        let source = KernelTemplates.zip2MapFilterTemplate (typeToStr firstGpuArray.Type) (typeToStr secondGpuArray.Type) resultTypeStr 
                                                                            varsStr (varExprToStr firstParamExpr vars) (varExprToStr secondParamExpr vars) 
                                                                            (varExprToStr context.CurrentVarExpr vars) (exprToStr bodyExpr vars)
                                                                            exprsStr (varExprToStr context.FlagVarExpr vars) (varExprToStr context.AccVarExpr vars)
                        { Source = source; ReductionType = context.ReductionType; Args = [| (firstGpuArray, firstGpuArray.Type, firstGpuArray.Length, firstGpuArray.Size); 
                                                                                            (secondGpuArray, secondGpuArray.Type, secondGpuArray.Length, secondGpuArray.Size) |] }
                    | _ -> failwithf "Not supported %A" context.ReductionType
                | Transform (Lambda ([paramExpr], bodyExpr), queryExpr') ->
                    let exprs' = assign context.CurrentVarExpr bodyExpr :: context.Exprs
                    compile' queryExpr' { context with CurrentVarExpr = paramExpr; VarExprs = paramExpr :: context.VarExprs; Exprs = exprs' }
                | Filter (Lambda ([paramExpr], bodyExpr), queryExpr') ->
                    match context.ReductionType with
                    | ReductionType.Map | ReductionType.Filter ->
                        let exprs' = ifThenElse bodyExpr (assign context.FlagVarExpr (constant 0)) (block [] [assign context.FlagVarExpr (constant 1); (``continue`` context.ContinueLabel)]) :: assign context.CurrentVarExpr paramExpr :: context.Exprs
                        compile' queryExpr' { context with CurrentVarExpr = paramExpr; VarExprs = paramExpr :: context.VarExprs; Exprs = exprs'; ReductionType = ReductionType.Filter } 
                    | _ ->
                        let exprs' = ifThenElse bodyExpr empty (``continue`` context.ContinueLabel) :: assign context.CurrentVarExpr paramExpr :: context.Exprs
                        compile' queryExpr' { context with CurrentVarExpr = paramExpr; VarExprs = paramExpr :: context.VarExprs; Exprs = exprs' } 
                | _ -> failwithf "Not supported %A" queryExpr 

            let finalVarExpr = var "___final___" queryExpr.Type
            let flagVarExpr = var "___flag___" typeof<int>
            match queryExpr with
            | Transform (_) ->
                let context = { CurrentVarExpr = finalVarExpr; AccVarExpr = finalVarExpr; FlagVarExpr = flagVarExpr;
                                BreakLabel = breakLabel (); ContinueLabel = continueLabel (); 
                                InitExprs = []; AccExpr = empty; CombinerExpr = empty; ResultType = queryExpr.Type; 
                                VarExprs = [finalVarExpr; flagVarExpr]; Exprs = []; ReductionType = ReductionType.Map  }
                compile' queryExpr context
            | Filter (_) ->
                let context = { CurrentVarExpr = finalVarExpr; AccVarExpr = finalVarExpr; FlagVarExpr = flagVarExpr;
                                BreakLabel = breakLabel (); ContinueLabel = continueLabel (); 
                                InitExprs = []; AccExpr = empty; CombinerExpr = empty; ResultType = queryExpr.Type; 
                                VarExprs = [finalVarExpr; flagVarExpr]; Exprs = []; ReductionType = ReductionType.Filter }
                compile' queryExpr context
            | Sum (queryExpr') ->
                let context = { CurrentVarExpr = finalVarExpr; AccVarExpr = finalVarExpr; FlagVarExpr = flagVarExpr;
                                BreakLabel = breakLabel (); ContinueLabel = continueLabel (); 
                                InitExprs = []; AccExpr = empty; CombinerExpr = empty; ResultType = queryExpr.Type; 
                                VarExprs = [finalVarExpr; flagVarExpr]; Exprs = []; ReductionType = ReductionType.Sum }
                compile' queryExpr' context
            | Count (queryExpr') ->
                let context = { CurrentVarExpr = finalVarExpr; AccVarExpr = finalVarExpr; FlagVarExpr = flagVarExpr;
                                BreakLabel = breakLabel (); ContinueLabel = continueLabel (); 
                                InitExprs = []; AccExpr = empty; CombinerExpr = empty; ResultType = queryExpr.Type; 
                                VarExprs = [finalVarExpr; flagVarExpr]; Exprs = []; ReductionType = ReductionType.Count }
                compile' (Transform ((lambda [|var "___empty___" queryExpr'.Type|] (constant 1)),queryExpr')) context
            | ToArray (queryExpr') -> compile queryExpr'
            | ZipWith (_, _, _) ->
                let context = { CurrentVarExpr = finalVarExpr; AccVarExpr = finalVarExpr; FlagVarExpr = flagVarExpr;
                                BreakLabel = breakLabel (); ContinueLabel = continueLabel (); 
                                InitExprs = []; AccExpr = empty; CombinerExpr = empty; ResultType = queryExpr.Type; 
                                VarExprs = [finalVarExpr; flagVarExpr]; Exprs = []; ReductionType = ReductionType.Map  }
                compile' queryExpr context
            | _ -> failwithf "Not supported %A" queryExpr 

