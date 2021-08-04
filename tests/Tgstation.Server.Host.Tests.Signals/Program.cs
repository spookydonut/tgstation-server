﻿using System;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mono.Unix.Native;
using Moq;

using Tgstation.Server.Host.Core;
using Tgstation.Server.Host.Extensions;
using Tgstation.Server.Host.System;

namespace Tgstation.Server.Host.Tests.Signals
{
	static class Program
	{
		static async Task Main()
		{
			var mockServerControl = new Mock<IServerControl>();

			var tcs = new TaskCompletionSource<object>();
			mockServerControl
				.Setup(x => x.GracefulShutdown())
				.Callback(() => tcs.SetResult(null))
				.Returns(Task.CompletedTask);

			var signalHandler = new PosixSignalHandler(mockServerControl.Object, Mock.Of<ILogger<PosixSignalHandler>>());

			Assert.IsFalse(tcs.Task.IsCompleted);

			await signalHandler.StartAsync(default);

			Assert.IsFalse(tcs.Task.IsCompleted);

			await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => signalHandler.StartAsync(default));

			Assert.IsFalse(tcs.Task.IsCompleted);

			using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
			await signalHandler.StopAsync(default).WithToken(cts.Token).ConfigureAwait(false);
			Assert.IsFalse(tcs.Task.IsCompleted);

			signalHandler = new PosixSignalHandler(mockServerControl.Object, Mock.Of<ILogger<PosixSignalHandler>>());
			await signalHandler.StartAsync(default);

			using var cts2 = new CancellationTokenSource(TimeSpan.FromSeconds(20));
			await tcs.Task.WithToken(cts2.Token);

			using var cts3 = new CancellationTokenSource(TimeSpan.FromSeconds(5));
			await signalHandler.StopAsync(default).WithToken(cts3.Token).ConfigureAwait(false);
		}
	}
}
