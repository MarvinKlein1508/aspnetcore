// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.IO.Pipelines;
using System.Linq;
using System.Net.Http.HPack;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Core.Internal;
using Microsoft.AspNetCore.Server.Kestrel.Core.Internal.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core.Internal.Http2;
using Microsoft.AspNetCore.Server.Kestrel.Core.Internal.Http3;
using Microsoft.AspNetCore.Server.Kestrel.Core.Internal.Infrastructure;
using Microsoft.AspNetCore.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;

namespace Microsoft.AspNetCore.Server.Kestrel.Microbenchmarks
{
    public abstract class Http3ConnectionBenchmarkBase
    {
        private Http3InMemory _http3;
        private IHeaderDictionary _httpRequestHeaders;
        private Http3HeadersEnumerator _requestHeadersEnumerator;

        protected abstract Task ProcessRequest(HttpContext httpContext);

        private class DefaultTimeoutHandler : ITimeoutHandler
        {
            public void OnTimeout(TimeoutReason reason) { }
        }

        public virtual void GlobalSetup()
        {
            _requestHeadersEnumerator = new Http3HeadersEnumerator();

            _httpRequestHeaders = new HttpRequestHeaders();
            _httpRequestHeaders[HeaderNames.Method] = new StringValues("GET");
            _httpRequestHeaders[HeaderNames.Path] = new StringValues("/");
            _httpRequestHeaders[HeaderNames.Scheme] = new StringValues("http");
            _httpRequestHeaders[HeaderNames.Authority] = new StringValues("localhost:80");

            var serviceContext = TestContextFactory.CreateServiceContext(
                serverOptions: new KestrelServerOptions(),
                dateHeaderValueManager: new DateHeaderValueManager(),
                systemClock: new MockSystemClock(),
                log: new MockTrace());
            serviceContext.DateHeaderValueManager.OnHeartbeat(default);

            var mockSystemClock = new Microsoft.AspNetCore.Testing.MockSystemClock();

            _http3 = new Http3InMemory(serviceContext, mockSystemClock, new DefaultTimeoutHandler());

            _http3.InitializeConnectionAsync(ProcessRequest).GetAwaiter().GetResult();
        }

        [Benchmark]
        public async Task MakeRequest()
        {
            _requestHeadersEnumerator.Initialize(_httpRequestHeaders);

            var stream = await _http3.CreateRequestStream();

            await stream.SendHeadersAsync(_requestHeadersEnumerator);

            while (true)
            {
                var frame = await stream.TryReceiveFrameAsync();
                if (frame == null)
                {
                    return;
                }

                switch (frame.Type)
                {
                    case System.Net.Http.Http3FrameType.Data:
                        break;
                    case System.Net.Http.Http3FrameType.Headers:
                        break;
                    default:
                        throw new InvalidOperationException($"Unexpected frame: {frame.Type}");
                }
            }
        }

        [GlobalCleanup]
        public void Dispose()
        {
        }
    }
}
