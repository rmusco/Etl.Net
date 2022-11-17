using System;
using System.Linq;
using System.Linq.Expressions;

namespace Paillave.Etl.Core.Mapping.Visitors
{
    public class MappingSetterVisitor : ExpressionVisitor
    {
        public MappingSetterDefinition MappingSetter = null;
        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            DummyFieldMapper dummyFieldMapper = new DummyFieldMapper();
            var methodInfo = node.Method;

            switch (node.NodeType)
            {
                case ExpressionType.Constant:
                case ExpressionType.Call:
                    methodInfo.Invoke(dummyFieldMapper, node.Arguments.Select(i => GetValue(i)).ToArray());
                    break;
                default:
                    throw new NotSupportedException($"Expression type {node.NodeType} not supported.");
            }

            this.MappingSetter = dummyFieldMapper.MappingSetter;
            return null;
        }

        private object GetValue(Expression member)
        {
            if (member is ConstantExpression)
                return ((ConstantExpression)member).Value;

            var objectMember = Expression.Convert(member, typeof(object));
            var getterLambda = Expression.Lambda<Func<object>>(objectMember);
            return getterLambda.Compile()();


        }
    }
}
