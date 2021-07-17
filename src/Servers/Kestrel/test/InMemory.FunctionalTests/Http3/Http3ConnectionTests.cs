// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Server.Kestrel.Core.Internal.Http3;
using Microsoft.AspNetCore.Testing;
using Microsoft.Net.Http.Headers;
using Xunit;
using Http3SettingType = Microsoft.AspNetCore.Server.Kestrel.Core.Internal.Http3.Http3SettingType;

namespace Microsoft.AspNetCore.Server.Kestrel.Core.Tests
{
    public class Http3ConnectionTests : Http3TestBase
    {
        [Fact]
        public async Task CreateRequestStream_RequestCompleted_Disposed()
        {
            var appCompletedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await Http3.InitializeConnectionAsync(async context =>
            {
                var buffer = new byte[16 * 1024];
                var received = 0;

                while ((received = await context.Request.Body.ReadAsync(buffer, 0, buffer.Length)) > 0)
                {
                    await context.Response.Body.WriteAsync(buffer, 0, received);
                }

                await appCompletedTcs.Task;
            });

            await Http3.CreateControlStream();
            await Http3.GetInboundControlStream();

            var requestStream = await Http3.CreateRequestStream();

            var headers = new[]
            {
                new KeyValuePair<string, string>(HeaderNames.Method, "Custom"),
                new KeyValuePair<string, string>(HeaderNames.Path, "/"),
                new KeyValuePair<string, string>(HeaderNames.Scheme, "http"),
                new KeyValuePair<string, string>(HeaderNames.Authority, "localhost:80"),
            };

            await requestStream.SendHeadersAsync(headers);
            await requestStream.SendDataAsync(Encoding.ASCII.GetBytes("Hello world"), endStream: true);

            Assert.False(requestStream.Disposed);

            appCompletedTcs.SetResult();
            await requestStream.ExpectHeadersAsync();
            var responseData = await requestStream.ExpectDataAsync();
            Assert.Equal("Hello world", Encoding.ASCII.GetString(responseData.ToArray()));

            Assert.True(requestStream.Disposed);
        }

        [Fact]
        public async Task GracefulServerShutdownClosesConnection()
        {
            await Http3.InitializeConnectionAsync(_echoApplication);

            var inboundControlStream = await Http3.GetInboundControlStream();
            await inboundControlStream.ExpectSettingsAsync();

            // Trigger server shutdown.
            Http3.CloseConnectionGracefully();

            Assert.Null(await Http3.MultiplexedConnectionContext.AcceptAsync().DefaultTimeout());

            await Http3.WaitForConnectionStopAsync(0, false, expectedErrorCode: Http3ErrorCode.NoError);
        }

        [Theory]
        [InlineData(0x0)]
        [InlineData(0x2)]
        [InlineData(0x3)]
        [InlineData(0x4)]
        [InlineData(0x5)]
        public async Task SETTINGS_ReservedSettingSent_ConnectionError(long settingIdentifier)
        {
            await Http3.InitializeConnectionAsync(_echoApplication);

            var outboundcontrolStream = await Http3.CreateControlStream();
            await outboundcontrolStream.SendSettingsAsync(new List<Http3PeerSetting>
            {
                new Http3PeerSetting((Http3SettingType) settingIdentifier, 0) // reserved value
            });

            await Http3.GetInboundControlStream();

            await Http3.WaitForConnectionErrorAsync<Http3ConnectionErrorException>(
                ignoreNonGoAwayFrames: true,
                expectedLastStreamId: 0,
                expectedErrorCode: Http3ErrorCode.SettingsError,
                expectedErrorMessage: CoreStrings.FormatHttp3ErrorControlStreamReservedSetting($"0x{settingIdentifier.ToString("X", CultureInfo.InvariantCulture)}"));
        }

        [Theory]
        [InlineData(0, "control")]
        [InlineData(2, "encoder")]
        [InlineData(3, "decoder")]
        public async Task InboundStreams_CreateMultiple_ConnectionError(int streamId, string name)
        {
            await Http3.InitializeConnectionAsync(_noopApplication);

            await Http3.CreateControlStream(streamId);
            await Http3.CreateControlStream(streamId);

            await Http3.WaitForConnectionErrorAsync<Http3ConnectionErrorException>(
                ignoreNonGoAwayFrames: true,
                expectedLastStreamId: 0,
                expectedErrorCode: Http3ErrorCode.StreamCreationError,
                expectedErrorMessage: CoreStrings.FormatHttp3ControlStreamErrorMultipleInboundStreams(name));
        }

        [Theory]
        [InlineData(nameof(Http3FrameType.Data))]
        [InlineData(nameof(Http3FrameType.Headers))]
        [InlineData(nameof(Http3FrameType.PushPromise))]
        public async Task ControlStream_ClientToServer_UnexpectedFrameType_ConnectionError(string frameType)
        {
            await Http3.InitializeConnectionAsync(_noopApplication);

            var controlStream = await Http3.CreateControlStream();

            var frame = new Http3RawFrame();
            frame.Type = Enum.Parse<Http3FrameType>(frameType);
            await controlStream.SendFrameAsync(frame, Memory<byte>.Empty);

            await Http3.WaitForConnectionErrorAsync<Http3ConnectionErrorException>(
                ignoreNonGoAwayFrames: true,
                expectedLastStreamId: 0,
                expectedErrorCode: Http3ErrorCode.UnexpectedFrame,
                expectedErrorMessage: CoreStrings.FormatHttp3ErrorUnsupportedFrameOnControlStream(Http3Formatting.ToFormattedType(frame.Type)));
        }

        [Fact]
        public async Task ControlStream_ClientToServer_ClientCloses_ConnectionError()
        {
            await Http3.InitializeConnectionAsync(_noopApplication);

            var controlStream = await Http3.CreateControlStream(id: 0);
            await controlStream.SendSettingsAsync(new List<Http3PeerSetting>());

            await controlStream.EndStreamAsync();

            await Http3.WaitForConnectionErrorAsync<Http3ConnectionErrorException>(
                ignoreNonGoAwayFrames: true,
                expectedLastStreamId: 0,
                expectedErrorCode: Http3ErrorCode.ClosedCriticalStream,
                expectedErrorMessage: CoreStrings.Http3ErrorControlStreamClientClosedInbound);
        }

        [Fact]
        public async Task ControlStream_ServerToClient_ErrorInitializing_ConnectionError()
        {
            Http3.OnCreateServerControlStream = testStreamContext =>
            {
                var controlStream = new Microsoft.AspNetCore.Testing.Http3ControlStream(Http3, testStreamContext);

                // Make server connection error when trying to write to control stream.
                controlStream.StreamContext.Transport.Output.Complete();

                return controlStream;
            };

            await Http3.InitializeConnectionAsync(_noopApplication);

            Http3.AssertConnectionError<Http3ConnectionErrorException>(
                expectedErrorCode: Http3ErrorCode.ClosedCriticalStream,
                expectedErrorMessage: CoreStrings.Http3ControlStreamErrorInitializingOutbound);
        }

        [Fact]
        public async Task SETTINGS_MaxFieldSectionSizeSent_ServerReceivesValue()
        {
            await Http3.InitializeConnectionAsync(_echoApplication);

            var inboundControlStream = await Http3.GetInboundControlStream();
            var incomingSettings = await inboundControlStream.ExpectSettingsAsync();

            var defaultLimits = new KestrelServerLimits();
            Assert.Collection(incomingSettings,
                kvp =>
                {
                    Assert.Equal((long)Http3SettingType.MaxFieldSectionSize, kvp.Key);
                    Assert.Equal(defaultLimits.MaxRequestHeadersTotalSize, kvp.Value);
                });

            var outboundcontrolStream = await Http3.CreateControlStream();
            await outboundcontrolStream.SendSettingsAsync(new List<Http3PeerSetting>
            {
                new Http3PeerSetting(Http3SettingType.MaxFieldSectionSize, 100)
            });

            var maxFieldSetting = await Http3.ServerReceivedSettingsReader.ReadAsync().DefaultTimeout();

            Assert.Equal(Http3SettingType.MaxFieldSectionSize, maxFieldSetting.Key);
            Assert.Equal(100, maxFieldSetting.Value);
        }

        [Fact]
        public async Task StreamPool_MultipleStreamsInSequence_PooledStreamReused()
        {
            var headers = new[]
            {
                new KeyValuePair<string, string>(HeaderNames.Method, "Custom"),
                new KeyValuePair<string, string>(HeaderNames.Path, "/"),
                new KeyValuePair<string, string>(HeaderNames.Scheme, "http"),
                new KeyValuePair<string, string>(HeaderNames.Authority, "localhost:80"),
            };

            await Http3.InitializeConnectionAsync(_echoApplication);

            var requestStream = await Http3.CreateRequestStream();
            var streamContext1 = requestStream.StreamContext;

            await requestStream.SendHeadersAsync(headers);
            await requestStream.SendDataAsync(Encoding.ASCII.GetBytes("Hello world 1"), endStream: true);

            Assert.False(requestStream.Disposed);

            await requestStream.ExpectHeadersAsync();
            var responseData = await requestStream.ExpectDataAsync();
            Assert.Equal("Hello world 1", Encoding.ASCII.GetString(responseData.ToArray()));

            await requestStream.ExpectReceiveEndOfStream();

            await requestStream.OnStreamCompletedTask.DefaultTimeout();

            Assert.True(requestStream.Disposed);

            requestStream = await Http3.CreateRequestStream();
            var streamContext2 = requestStream.StreamContext;

            await requestStream.SendHeadersAsync(headers);
            await requestStream.SendDataAsync(Encoding.ASCII.GetBytes("Hello world 2"), endStream: true);

            Assert.False(requestStream.Disposed);

            await requestStream.ExpectHeadersAsync();
            responseData = await requestStream.ExpectDataAsync();
            Assert.Equal("Hello world 2", Encoding.ASCII.GetString(responseData.ToArray()));

            await requestStream.ExpectReceiveEndOfStream();

            Assert.True(requestStream.Disposed);

            Assert.Same(streamContext1, streamContext2);
        }
    }
}
