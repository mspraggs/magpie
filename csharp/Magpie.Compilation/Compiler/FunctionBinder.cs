﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Magpie.Compilation
{
    /// <summary>
    /// Generates a bound function body where all names have been resolved.
    /// </summary>
    public class FunctionBinder : IUnboundExprVisitor<IBoundExpr>
    {
        public static void Bind(Compiler compiler, BoundFunction function, Function instancingContext)
        {
            var scope = new Scope();

            // create a local slot for the arg
            if (function.Type.Parameters.Count > 0)
            {
                scope.Define("__arg", Decl.Unit /* ignored */, false);
            }

            var binder = new FunctionBinder(function.Unbound, instancingContext, compiler, scope);

            //### bob: also need to bind the function's type signature so that named type references
            //         can be properly fully-qualified

            // bind the function
            IBoundExpr body = function.Unbound.Body.Accept<IBoundExpr>(binder);
            function.Bind(body, scope.NumVariables);

            // make sure declared return type matches actual return type
            // ignore wrap bound expressions. those are used for the autogenerated functions from types
            // since types aren't instantiated for generics, the expr type will be wrong
            if (!(function.Unbound.Body is WrapBoundExpr) && !DeclComparer.TypesMatch(function.Type.Return, body.Type))
            {
                throw new CompileException(String.Format("{0} is declared to return {1} but is returning {2}.",
                    function.Name, function.Type.Return, function.Body.Type));
            }
        }

        #region IUnboundExprVisitor<IBoundExpr> Members

        public IBoundExpr Visit(CallExpr expr)
        {
            IBoundExpr boundArg = expr.Arg.Accept(this);

            NameExpr namedTarget = expr.Target as NameExpr;
            if (namedTarget != null)
            {
                return mCompiler.ResolveName(mFunction, mInstancingContext, Scope, namedTarget.Name, namedTarget.TypeArgs, boundArg);
            }

            IBoundExpr target = expr.Target.Accept(this);

            // if the "function" is an int, the arg can be an array
            if (target.Type == Decl.Int)
            {
                if (boundArg.Type is ArrayType)
                {
                    // array access
                    return new LoadElementExpr(boundArg, target);
                }
                else
                {
                    throw new CompileException("Integers can only be called with an array argument.");
                }
            }
            else if(!(target.Type is FuncType)) throw new CompileException("Target of an application must be a function.");

            // check that args match
            FuncType funcType = (FuncType)target.Type;
            if (!DeclComparer.TypesMatch(funcType.ParameterTypes, boundArg.Type.Expanded))
            {
                throw new CompileException("Argument types passed to evaluated function reference do not match function's parameter types.");
            }

            // simply apply the arg to the bound expression
            return new BoundCallExpr(target, boundArg);
        }

        public IBoundExpr Visit(ArrayExpr expr)
        {
            Decl elementType = expr.ElementType;
            IEnumerable<IBoundExpr> elements = expr.Elements.Accept(this);

            // infer the type from the elements
            if (elementType == null)
            {
                int index = 0;
                foreach (var element in elements)
                {
                    if (elementType == null)
                    {
                        // take the type of the first
                        elementType = element.Type;
                    }
                    else
                    {
                        // make sure the others match
                        if (!DeclComparer.TypesMatch(elementType, element.Type))
                            throw new CompileException(String.Format("Array elements must all be the same type. Array is type {0}, but element {1} is type {2}.",
                                elementType, index, element.Type));
                    }

                    index++;
                }
            }

            //### bob: need to check all elements are the same type
            return new BoundArrayExpr(elementType, elements, expr.IsMutable);
        }

        public IBoundExpr Visit(AssignExpr expr)
        {
            IBoundExpr value = expr.Value.Accept(this);

            // handle a name target: foo <- 3
            NameExpr nameTarget = expr.Target as NameExpr;
            if (nameTarget != null)
            {
                // see if it's a local
                if (Scope.Contains(nameTarget.Name))
                {
                    if (!Scope[nameTarget.Name].IsMutable) throw new CompileException(expr.Position, "Cannot assign to immutable local.");

                    // direct assign to local
                    return new StoreExpr(new LocalsExpr(), Scope[nameTarget.Name], value);
                }

                // look for an assignment function
                return TranslateAssignment(nameTarget.Name, nameTarget.TypeArgs, new UnitExpr(TokenPosition.None), value);
            }

            // handle a function apply target: Foo 1 <- 3  ==> Foo<- (1, 3)
            CallExpr callTarget = expr.Target as CallExpr;
            if (callTarget != null)
            {
                IBoundExpr callArg = callTarget.Arg.Accept(this);

                // see if it's a direct function call
                NameExpr funcName = callTarget.Target as NameExpr;
                if (funcName != null)
                {
                    // translate the call
                    return TranslateAssignment(funcName.Name, funcName.TypeArgs, callArg, value);
                }

                //### bob: in progress array assignment
                IBoundExpr boundCallTarget = callTarget.Target.Accept(this);

                // if the target evaluates to an int and the arg is an array, it's an array set
                if ((boundCallTarget.Type == Decl.Int) && (callArg.Type is ArrayType))
                {
                    ArrayType arrayArgType = (ArrayType)callArg.Type;
                    if (!arrayArgType.IsMutable) throw new CompileException(expr.Position, "Cannot set an element in an immutable array.");

                    // array access
                    return new StoreElementExpr(callArg, boundCallTarget, value);
                }

                throw new CompileException("Couldn't figure out what you're trying to do on the left side of an assignment.");
            }

            // handle an operator target: 1 $$ 2 <- 3  ==> $$<- (1, 2, 3)
            OperatorExpr operatorTarget = expr.Target as OperatorExpr;
            if (operatorTarget != null)
            {
                IBoundExpr opArg = new BoundTupleExpr(new IBoundExpr[]
                    { operatorTarget.Left.Accept(this),
                      operatorTarget.Right.Accept(this) });

                return TranslateAssignment(operatorTarget.Name, null /* no operator generics yet */, opArg, value);
            }

            TupleExpr tupleTarget = expr.Target as TupleExpr;
            if (tupleTarget != null)
            {
                //### bob: need to handle tuple decomposition here:
                //         a, b <- (1, 2)
                throw new NotImplementedException();
            }

            // if we got here, it's not a valid assignment expression
            throw new CompileException("Cannot assign to " + expr.Target);
        }

        public IBoundExpr Visit(BlockExpr block)
        {
            // create an inner scope
            mScope.Push();

            List<IBoundExpr> exprs = new List<IBoundExpr>();
            int index = 0;
            foreach (IUnboundExpr expr in block.Exprs)
            {
                IBoundExpr bound = expr.Accept(this);

                // all but last expression must be void
                if (index < block.Exprs.Count - 1)
                {
                    if (bound.Type != Decl.Unit) throw new CompileException(expr.Position, "All expressions in a block except the last must be of type Unit. " + block.ToString());
                }

                index++;

                exprs.Add(bound);
            }

            mScope.Pop();

            return new BoundBlockExpr(exprs);
        }

        public IBoundExpr Visit(DefineExpr expr)
        {
            if (Scope.Contains(expr.Name)) throw new CompileException(expr.Position, "A local variable named \"" + expr.Name + "\" is already defined in this scope.");

            IBoundExpr value = expr.Value.Accept(this);

            // add it to the scope
            Scope.Define(expr.Name, value.Type, expr.IsMutable);

            // assign it
            return new StoreExpr(new LocalsExpr(), Scope[expr.Name], value);
        }

        public IBoundExpr Visit(FuncRefExpr expr)
        {
            ICallable callable = mCompiler.FindFunction(mFunction, mInstancingContext,
                expr.Name.Name, expr.Name.TypeArgs, expr.ParamTypes);

            BoundFunction bound = callable as BoundFunction;

            //### bob: to support intrinsics, we'll need to basically create wrapper functions
            // that have the same type signature as the intrinsic and that do nothing but
            // call the intrinsic and return. then, we can get a reference to that wrapper.
            // 
            // to support foreign functions, we can either do the same thing, or change the
            // way function references work. if a function reference can be distinguished
            // between being a regular function, a foreign one (or later a closure), then
            // we can get rid of ForeignFuncCallExpr and just use CallExpr for foreign calls
            // too.
            if (bound == null) throw new NotImplementedException("Can only get references to user-defined or auto-generated functions. Intrinsics and foreign function references aren't supported yet.");

            return new BoundFuncRefExpr(bound);
        }

        public IBoundExpr Visit(IfThenExpr expr)
        {
            var bound = new BoundIfThenExpr(
                expr.Condition.Accept(this),
                expr.Body.Accept(this));

            if (bound.Body.Type != Decl.Unit)
            {
                throw new CompileException(String.Format(
                    "Body of if/do is returning type {0} but should be void.",
                    bound.Body.Type));
            }

            return bound;
        }

        public IBoundExpr Visit(IfThenElseExpr expr)
        {
            var bound = new BoundIfThenElseExpr(
                expr.Condition.Accept(this),
                expr.ThenBody.Accept(this),
                expr.ElseBody.Accept(this));

            if (!DeclComparer.TypesMatch(bound.ThenBody.Type, bound.ElseBody.Type))
            {
                throw new CompileException(String.Format(
                    "Arms of if/then/else to not return the same type. Then arm returns {0} while else arm returns {1}.",
                    bound.ThenBody.Type, bound.ElseBody.Type));
            }

            return bound;
        }

        public IBoundExpr Visit(NameExpr expr)
        {
            return mCompiler.ResolveName(mFunction, mInstancingContext, Scope, expr.Name, expr.TypeArgs, null);
        }

        public IBoundExpr Visit(OperatorExpr expr)
        {
            // an operator is just function application
            CallExpr apply = new CallExpr(new NameExpr(expr.Position, expr.Name), new TupleExpr(new IUnboundExpr[] { expr.Left, expr.Right }));

            return apply.Accept(this);
        }

        public IBoundExpr Visit(TupleExpr expr)
        {
            return new BoundTupleExpr(expr.Fields.Accept(this));
        }

        public IBoundExpr Visit(IntExpr expr)
        {
            return expr;
        }

        public IBoundExpr Visit(BoolExpr expr)
        {
            return expr;
        }

        public IBoundExpr Visit(StringExpr expr)
        {
            return expr;
        }

        public IBoundExpr Visit(UnitExpr expr)
        {
            return expr;
        }

        public IBoundExpr Visit(WhileExpr expr)
        {
            var bound = new BoundWhileExpr(expr.Condition.Accept(this), expr.Body.Accept(this));

            if (bound.Condition.Type != Decl.Bool)
            {
                throw new CompileException(String.Format(
                    "Condition of while/do is returning type {0} but should be Bool.",
                    bound.Body.Type));
            }

            if (bound.Body.Type != Decl.Unit)
            {
                throw new CompileException(String.Format(
                    "Body of while/do is returning type {0} but should be Unit.",
                    bound.Body.Type));
            }

            return bound;
        }

        public IBoundExpr Visit(ConstructUnionExpr expr)
        {
            return expr;
        }

        public IBoundExpr Visit(WrapBoundExpr expr)
        {
            // unwrap it
            return expr.Bound;
        }

        #endregion

        private IBoundExpr TranslateAssignment(string baseName, IList<Decl> typeArgs, IBoundExpr arg, IBoundExpr value)
        {
            string name = baseName + "<-";

            // add the value argument
            arg = arg.AppendArg(value);

            return mCompiler.ResolveName(mFunction, mInstancingContext, Scope, name, typeArgs, arg);
        }

        private FunctionBinder(Function function, Function instancingContext, Compiler compiler, Scope scope)
        {
            mFunction = function;
            mInstancingContext = instancingContext;
            mCompiler = compiler;
            mScope = scope;
        }

        private Scope Scope { get { return mScope; } }

        private Function mFunction;
        private Function mInstancingContext;
        private Compiler mCompiler;
        private Scope mScope;
    }
}
