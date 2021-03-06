// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Diagnostics.ContractsLight;
using TypeScript.Net.Utilities;

namespace BuildXL.FrontEnd.Script.Expressions
{
    /// <summary>
    /// Base class for <see cref="AssignmentExpression"/> and <see cref="IncrementDecrementExpression"/>.
    /// </summary>
    /// <remarks>
    /// This class encodes in the type system the restriction for loop incrementers.
    /// </remarks>
    public abstract class AssignmentOrIncrementDecrementExpression : Expression
    {
        /// <nodoc />
        protected AssignmentOrIncrementDecrementExpression(LineInfo location)
            : base(location)
        {
            Contract.Assume(this is AssignmentExpression || this is IncrementDecrementExpression);
        }
    }
}
