﻿using Accelist.FluentValidation.Blazor;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using FluentValidation.Internal;
using System.Threading.Tasks;
using System.Linq;

namespace Microsoft.AspNetCore.Components.Forms
{
    /// <summary>
    /// Add Fluent Validator support to an EditContext.
    /// </summary>
    public class FluentValidator : ComponentBase, IDisposable
    {
        /// <summary>
        /// Inherited object from the FormEdit component.
        /// </summary>
        [CascadingParameter]
        private EditContext CurrentEditContext { get; set; }

        /// <summary>
        /// Enable access to the ASP.NET Core Service Provider / DI.
        /// </summary>
        [Inject]
        private IServiceProvider InjectedServiceProvider { get; set; }

        [CascadingParameter]
        private IServiceProvider CascadingServiceProvider { get; set; }

        private IServiceProvider ServiceProvider { get; set; }
        private IServiceScope ServiceScope { get; set; }
        private ValidationMessageStore MessageStore { get; set; }

        /// <summary>
        /// The AbstractValidator object for the corresponding form Model object type.
        /// </summary>
        [Parameter]
        public IValidator Validator { set; get; }

        /// <summary>
        /// The AbstractValidator objects mapping for each children / nested object validators.
        /// </summary>
        [Parameter]
        public Dictionary<Type, IValidator> ChildValidators { set; get; } = new Dictionary<Type, IValidator>();

        [Parameter]
        public bool ValidateModelOnFieldChange { set; get; }

        [Parameter]
        public bool SuppressCascadingServiceProvider { set; get; }

        [Parameter]
        public bool UseServiceScope { set; get; }

        [Parameter]
        public string ValidatorRuleSets { get; set; }

        /// <summary>
        /// Attach to parent EditForm context enabling validation.
        /// </summary>
        protected override void OnInitialized()
        {
            if (CurrentEditContext == null)
            {
                throw new InvalidOperationException($"{nameof(DataAnnotationsValidator)} requires a cascading " +
                    $"parameter of type {nameof(EditContext)}. For example, you can use {nameof(DataAnnotationsValidator)} " +
                    $"inside an EditForm.");
            }

            MessageStore = new ValidationMessageStore(CurrentEditContext);

            if (CascadingServiceProvider is not null && !SuppressCascadingServiceProvider)
            {
                ServiceProvider = CascadingServiceProvider;
            }
            else
            {
                ServiceProvider = InjectedServiceProvider;
            }

            if (UseServiceScope)
            {
                ServiceScope = ServiceProvider.CreateScope();
                ServiceProvider = ServiceScope.ServiceProvider;
            }

            if (this.Validator == null)
            {
                this.SetFormValidator();
            }

            this.AddValidation();
        }

        /// <summary>
        /// Try setting the EditContext form model typed validator implementation from the DI.
        /// </summary>
        private void SetFormValidator()
        {
            var formType = CurrentEditContext.Model.GetType();
            this.Validator = TryGetValidatorForObjectType(formType);
            if (this.Validator == null)
            {
                throw new InvalidOperationException($"FluentValidation.IValidator<{formType.FullName}> is"
                    + " not registered in the application service provider.");
            }
        }

        /// <summary>
        /// Try acquiring the typed validator implementation from the DI.
        /// </summary>
        /// <param name="modelType"></param>
        /// <returns></returns>
        private IValidator TryGetValidatorForObjectType(Type modelType)
        {
            var validatorType = typeof(IValidator<>);
            var formValidatorType = validatorType.MakeGenericType(modelType);
            return ServiceProvider.GetService(formValidatorType) as IValidator;
        }

        /// <summary>
        /// Creates an instance of a ValidationContext for an object model.
        /// </summary>
        /// <param name="model"></param>
        /// <param name="validatorSelector"></param>
        /// <returns></returns>
        private IValidationContext CreateValidationContext(object model, IEnumerable<string> properties, IEnumerable<string> ruleSets)
        {
            // Don't need to use reflection to construct the context. 
            // If you create it as a ValidationContext<object> instead of a ValidationContext<T> then FluentValidation will perform the conversion internally, assuming the types are compatible. 
            var context = ValidationContext<object>.CreateWithOptions(model, opts =>
            {
                var selectors = new List<IValidatorSelector>(2);

                if (properties is not null && properties.Any())
                {
                    selectors.Add(ValidatorOptions.Global.ValidatorSelectors.MemberNameValidatorSelectorFactory(properties.ToArray()));
                }

                if (ruleSets is not null && ruleSets.Any())
                {
                    selectors.Add(ValidatorOptions.Global.ValidatorSelectors.RulesetValidatorSelectorFactory(ruleSets.ToArray()));
                }

                if (selectors.Any())
                {
                    opts.UseCustomSelector(selectors.Count == 1 ? selectors[0] : new CompositeValidatorSelector(selectors));
                }
            });

            // InjectValidator looks for a service provider inside the ValidationContext with this key. 
            context.RootContextData["_FV_ServiceProvider"] = ServiceProvider;
            return context;
        }

        /// <summary>
        /// Add form validation logic handlers.
        /// </summary>
        private void AddValidation()
        {             
            // Perform object-level validation on request
            CurrentEditContext.OnValidationRequested +=
                (sender, eventArgs) => _ = ValidateModel(ValidatorRuleSets);

            // Perform per-field validation on each field edit
            CurrentEditContext.OnFieldChanged += (sender, eventArgs) =>
            {
                if (ValidateModelOnFieldChange)
                {
                    _ = ValidateModel(ValidatorRuleSets);
                }
                else
                {
                    ValidateField(CurrentEditContext, eventArgs.FieldIdentifier);
                }
            };
        }

        public Task<bool> ValidateModel(string rulesets)
        {
            return ValidateModel(rulesets?.Split(','));
        }

        public Task<bool> ValidateModel(params string[] rulesets)
        {
            return ValidateModel(rulesets?.AsEnumerable());
        }

        /// <summary>
        /// Validate the whole form and trigger client UI update.
        /// </summary>
        /// <param name="editContext"></param>
        /// <param name="messages"></param>
        public async Task<bool> ValidateModel(IEnumerable<string> ruleSets = null)
        {
            // <EditForm> should now be able to run async validations:
            // https://github.com/dotnet/aspnetcore/issues/11914
            var validationResults = await TryValidateModel(CurrentEditContext, ruleSets);
            MessageStore.Clear();

            var graph = new ModelGraphCache(CurrentEditContext.Model);
            foreach (var error in validationResults.Errors)
            {
                var (propertyValue, propertyName) = graph.EvalObjectProperty(error.PropertyName);
                // while it is impossible to have a validation error for a null child property, better be safe than sorry...
                if (propertyValue != null)
                {
                    var fieldID = new FieldIdentifier(propertyValue, propertyName);
                    MessageStore.Add(fieldID, error.ErrorMessage);
                }
            }

            CurrentEditContext.NotifyValidationStateChanged();

            return CurrentEditContext.GetValidationMessages().Any();
        }

        /// <summary>
        /// Attempts to validate an entire form object model.
        /// </summary>
        /// <param name="editContext"></param>
        /// <returns></returns>
        private async Task<ValidationResult> TryValidateModel(EditContext editContext, IEnumerable<string> ruleSets)
        {
            try
            {
                var validationContext = CreateValidationContext(editContext.Model, null, ruleSets);
                return await ValidatorValidate(Validator, validationContext);
            }
            catch (Exception ex)
            {
                var msg = $"An unhandled exception occurred when validating <EditForm> model type: '{editContext.Model.GetType()}'";
                throw new UnhandledValidationException(msg, ex);
            }
        }

        /// <summary>
        /// Attempts to validate a single field or property of a form model or child object model.
        /// </summary>
        /// <param name="validator"></param>
        /// <param name="editContext"></param>
        /// <param name="fieldIdentifier"></param>
        /// <returns></returns>
        private async Task<ValidationResult> TryValidateField(IValidator validator, EditContext editContext, FieldIdentifier fieldIdentifier)
        {
            try
            {
                var vctx = CreateValidationContext(fieldIdentifier.Model, new[] { fieldIdentifier.FieldName }, ValidatorRuleSets?.Split(','));
                return await ValidatorValidate(validator, vctx);
            }
            catch (Exception ex)
            {
                var msg = $"An unhandled exception occurred when validating field name: '{fieldIdentifier.FieldName}'";

                if (editContext.Model != fieldIdentifier.Model)
                {
                    msg += $" of a child object of type: {fieldIdentifier.Model.GetType()}";
                }

                msg += $" of <EditForm> model type: '{editContext.Model.GetType()}'";
                throw new UnhandledValidationException(msg, ex);
            }
        }

        /// <summary>
        /// Attempts to retrieve the field or property validator of a form model or child object model.
        /// </summary>
        /// <param name="editContext"></param>
        /// <param name="fieldIdentifier"></param>
        /// <returns></returns>
        private IValidator TryGetFieldValidator(EditContext editContext, in FieldIdentifier fieldIdentifier)
        {
            if (fieldIdentifier.Model == editContext.Model)
            {
                return Validator;
            }

            var modelType = fieldIdentifier.Model.GetType();
            if (ChildValidators.ContainsKey(modelType))
            {
                return ChildValidators[modelType];
            }

            var validator = TryGetValidatorForObjectType(modelType);
            ChildValidators[modelType] = validator;
            return validator;
        }

        /// <summary>
        /// Validate a single field and trigger client UI update.
        /// </summary>
        /// <param name="editContext"></param>
        /// <param name="messages"></param>
        /// <param name="fieldIdentifier"></param>
        private async void ValidateField(EditContext editContext, FieldIdentifier fieldIdentifier)
        {
            var fieldValidator = TryGetFieldValidator(editContext, fieldIdentifier);
            if (fieldValidator == null)
            {
                // Should not error / just fail silently for classes not supposed to be validated.
                return;
            }

            var validationResults = await TryValidateField(fieldValidator, editContext, fieldIdentifier);
            MessageStore.Clear(fieldIdentifier);

            foreach (var error in validationResults.Errors)
            {
                MessageStore.Add(fieldIdentifier, error.ErrorMessage);
            }

            editContext.NotifyValidationStateChanged();
        }

        protected virtual async Task<ValidationResult> ValidatorValidate(IValidator validator, IValidationContext context)
        {
            return await validator.ValidateAsync(context);
        }

        #region IDisposable Support
        private bool disposedValue = false; // To detect redundant calls

        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    // Dispose managed state (managed objects).
                    ServiceScope?.Dispose();
                }

                // Free unmanaged resources (unmanaged objects) and override a finalizer below.

                // Set large fields to null.
                ServiceProvider = null;
                ServiceScope = null;
                Validator = null;
                ChildValidators = null;
                MessageStore = null;

                disposedValue = true;
            }
        }

        // Override a finalizer only if Dispose(bool disposing) above has code to free unmanaged resources.
        // ~FluentValidator()
        // {
        //   // Do not change this code. Put cleanup code in Dispose(bool disposing) above.
        //   Dispose(false);
        // }

        // This code added to correctly implement the disposable pattern.
        public void Dispose()
        {
            // Do not change this code. Put cleanup code in Dispose(bool disposing) above.
            Dispose(true);
            // Uncomment the following line if the finalizer is overridden above.
            // GC.SuppressFinalize(this);
        }
        #endregion
    }
}