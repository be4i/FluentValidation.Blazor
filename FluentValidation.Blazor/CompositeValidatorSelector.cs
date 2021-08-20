using FluentValidation;
using FluentValidation.Internal;
using System.Collections.Generic;
using System.Linq;

namespace Accelist.FluentValidation.Blazor
{
    public class CompositeValidatorSelector : IValidatorSelector
    {
        private IEnumerable<IValidatorSelector> _selectors;

        public CompositeValidatorSelector(IEnumerable<IValidatorSelector> selectors)
        {
            _selectors = selectors;
        }

        public bool CanExecute(IValidationRule rule, string propertyPath, IValidationContext context)
        {
            return _selectors.All(s => s.CanExecute(rule, propertyPath, context));
        }
    }
}
