// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

namespace Microsoft.AspNetCore.Components.WebView.Photino
{
    /// <summary>
    /// Configures root components for a <see cref="BlazorWindow"/>.
    /// </summary>
    public sealed class BlazorWindowRootComponents
    {
        private readonly PhotinoWebViewManager _manager;

        internal BlazorWindowRootComponents(PhotinoWebViewManager manager)
        {
            _manager = manager;
        }

        /// <summary>
        /// Adds a root component to the window.
        /// </summary>
        /// <typeparam name="TComponent">The component type.</typeparam>
        /// <param name="selector">A CSS selector describing where the component should be added in the host page.</param>
        /// <param name="parameters">An optional dictionary of parameters to pass to the component.</param>
        public void Add<TComponent>(string selector, IDictionary<string, object?>? parameters = null) where TComponent : IComponent
        {
            var parameterView = parameters == null
                ? ParameterView.Empty
                : ParameterView.FromDictionary(parameters);

            // Dispatch because this is going to be async, and we want to catch any errors
            _ = _manager.Dispatcher.InvokeAsync(async () =>
            {
                await _manager.AddRootComponentAsync(typeof(TComponent), selector, parameterView);
            });
        }
    }
}
