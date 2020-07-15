using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Tgstation.Server.Api;
using Tgstation.Server.Api.Models;
using Tgstation.Server.Client;
using Tgstation.Server.Host.Configuration;
using Tgstation.Server.Host.Database;
using Tgstation.Server.Host.Database.Migrations;
using Tgstation.Server.Host.Extensions;
using Tgstation.Server.Host.System;
using Tgstation.Server.Tests.Instance;

namespace Tgstation.Server.Tests
{
	[TestClass]
	[TestCategory("SkipWhenLiveUnitTesting")]
	public sealed class IntegrationTest
	{
		readonly IServerClientFactory clientFactory = new ServerClientFactory(new ProductHeaderValue(Assembly.GetExecutingAssembly().GetName().Name, Assembly.GetExecutingAssembly().GetName().Version.ToString()));

		[TestMethod]
		public async Task TestUpdateProtocol()
		{
			using var server = new TestingServer();
			using var serverCts = new CancellationTokenSource();
			var cancellationToken = serverCts.Token;
			var serverTask = server.Run(cancellationToken);
			try
			{
				var testUpdateVersion = new Version(4, 3, 0);
				using (var adminClient = await CreateAdminClient(server.Url, cancellationToken))
					//attempt to update to stable
					await adminClient.Administration.Update(new Administration
					{
						NewVersion = testUpdateVersion
					}, cancellationToken).ConfigureAwait(false);

				//wait up to 3 minutes for the dl and install
				await Task.WhenAny(serverTask, Task.Delay(TimeSpan.FromMinutes(3), cancellationToken)).ConfigureAwait(false);

				Assert.IsTrue(serverTask.IsCompleted, "Server still running!");

				Assert.IsTrue(Directory.Exists(server.UpdatePath), "Update directory not present!");

				var updatedAssemblyPath = Path.Combine(server.UpdatePath, "Tgstation.Server.Host.dll");
				Assert.IsTrue(File.Exists(updatedAssemblyPath), "Updated assembly missing!");

				var updatedAssemblyVersion = FileVersionInfo.GetVersionInfo(updatedAssemblyPath);
				Assert.AreEqual(testUpdateVersion, Version.Parse(updatedAssemblyVersion.FileVersion).Semver());
			}
			finally
			{
				serverCts.Cancel();
				try
				{
					await serverTask.ConfigureAwait(false);
				}
				catch (OperationCanceledException) { }
				catch (AggregateException ex)
				{
					if (ex.InnerException is NotSupportedException notSupportedException)
						Assert.Inconclusive(notSupportedException.Message);
				}
			}
			Assert.IsTrue(server.RestartRequested, "Server not requesting restart!");
		}

		static void TerminateAllDDs()
		{
			foreach (var proc in System.Diagnostics.Process.GetProcessesByName("DreamDaemon"))
				using (proc)
					proc.Kill();
		}

		async Task<IServerClient> CreateAdminClient(Uri url, CancellationToken cancellationToken)
		{
			var giveUpAt = DateTimeOffset.Now.AddSeconds(60);
			do
			{
				try
				{
					return await clientFactory.CreateFromLogin(url, User.AdminName, User.DefaultAdminPassword, attemptLoginRefresh: false).ConfigureAwait(false);
				}
				catch (HttpRequestException)
				{
					//migrating, to be expected
					if (DateTimeOffset.Now > giveUpAt)
						throw;
					await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
				}
				catch (ServiceUnavailableException)
				{
					// migrating, to be expected
					if (DateTimeOffset.Now > giveUpAt)
						throw;
					await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
				}
			} while (true);
		}

#if DEBUG
		[TestMethod]
		public async Task TestDownMigrations()
		{
			var connectionString = Environment.GetEnvironmentVariable("TGS4_TEST_CONNECTION_STRING");

			if (String.IsNullOrEmpty(connectionString))
				Assert.Inconclusive("No connection string configured in env var TGS4_TEST_CONNECTION_STRING!");

			var databaseTypeString = Environment.GetEnvironmentVariable("TGS4_TEST_DATABASE_TYPE");
			if (!Enum.TryParse<DatabaseType>(databaseTypeString, out var databaseType))
				Assert.Inconclusive("No/invalid database type configured in env var TGS4_TEST_DATABASE_TYPE!");

			string migrationName = null;
			DbContext CreateContext()
			{
				switch (databaseType)
				{
					case DatabaseType.MySql:
					case DatabaseType.MariaDB:
						migrationName = nameof(MYInitialCreate);
						return new MySqlDatabaseContext(Host.Database.Design.DesignTimeDbContextFactoryHelpers.CreateDatabaseContextOptions<MySqlDatabaseContext>(databaseType, connectionString));
					case DatabaseType.PostgresSql:
						migrationName = nameof(PGCreate);
						return new PostgresSqlDatabaseContext(Host.Database.Design.DesignTimeDbContextFactoryHelpers.CreateDatabaseContextOptions<PostgresSqlDatabaseContext>(databaseType, connectionString));
					case DatabaseType.SqlServer:
						migrationName = nameof(MSInitialCreate);
						return new SqlServerDatabaseContext(Host.Database.Design.DesignTimeDbContextFactoryHelpers.CreateDatabaseContextOptions<SqlServerDatabaseContext>(databaseType, connectionString));
					case DatabaseType.Sqlite:
						migrationName = nameof(SLRebuild);
						return new SqliteDatabaseContext(Host.Database.Design.DesignTimeDbContextFactoryHelpers.CreateDatabaseContextOptions<SqliteDatabaseContext>(databaseType, connectionString));
				}

				return null;
			}

			Task Delete(DbContext context) => databaseType == DatabaseType.Sqlite ? Task.CompletedTask : context.Database.EnsureCreatedAsync();

			using var context = CreateContext();
			await Delete(context);
			await context.Database.MigrateAsync(default);
			var dbServiceProvider = ((IInfrastructure<IServiceProvider>)context.Database).Instance;
			var migrator = dbServiceProvider.GetRequiredService<IMigrator>();
			await migrator.MigrateAsync(migrationName, default);
			await Delete(context);
		}
#endif

		[TestMethod]
		public async Task TestServer()
		{
			var procs = System.Diagnostics.Process.GetProcessesByName("byond");
			if(procs.Any())
			{
				foreach (var proc in procs)
					proc.Dispose();
				Assert.Inconclusive("Cannot run server test because DreamDaemon will not start headless while the BYOND pager is running!");
			}

			using var server = new TestingServer();

			using var hardTimeoutCts = new CancellationTokenSource();

			var maximumTestDuration = new TimeSpan(0, 12, 0);

			hardTimeoutCts.CancelAfter(maximumTestDuration - new TimeSpan(0, 0, 15));
			var hardTimeoutCancellationToken = hardTimeoutCts.Token;
			hardTimeoutCancellationToken.Register(() =>
			{
				Console.WriteLine($"[{DateTimeOffset.Now}] TEST TIMEOUT HARD!");
			});

			using var softTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(hardTimeoutCancellationToken);
			softTimeoutCts.CancelAfter(maximumTestDuration - new TimeSpan(0, 0, 45));
			var softTimeoutCancellationToken = softTimeoutCts.Token;
			bool tooLateForSoftTimeout = false;
			softTimeoutCancellationToken.Register(() =>
			{
				if (!tooLateForSoftTimeout)
					Console.WriteLine($"[{DateTimeOffset.Now}] TEST TIMEOUT SOFT!");
			});

			using var serverCts = CancellationTokenSource.CreateLinkedTokenSource(softTimeoutCancellationToken);
			var cancellationToken = serverCts.Token;

			TerminateAllDDs();
			var serverTask = server.Run(cancellationToken);

			try
			{
				Api.Models.Instance instance;
				using (var adminClient = await CreateAdminClient(server.Url, cancellationToken))
				{
					if (server.DumpOpenApiSpecpath)
					{
						// Dump swagger to disk
						// This is purely for CI
						var webRequest = WebRequest.Create(server.Url.ToString() + "swagger/v1/swagger.json");
						using var response = webRequest.GetResponse();
						using var content = response.GetResponseStream();
						using var output = new FileStream(@"C:\swagger.json", FileMode.Create);
						await content.CopyToAsync(output);
					}

					async Task FailFast(Task task)
					{
						try
						{
							await task;
						}
						catch (OperationCanceledException)
						{
							throw;
						}
						catch (Exception ex)
						{
							Console.WriteLine($"[{DateTimeOffset.Now}] TEST ERROR: {ex}");
							serverCts.Cancel();
							throw;
						}
					}

					var rootTest = FailFast(new RootTest().Run(clientFactory, adminClient, cancellationToken));
					var adminTest = FailFast(new AdministrationTest(adminClient.Administration).Run(cancellationToken));
					var usersTest = FailFast(new UsersTest(adminClient.Users).Run(cancellationToken));
					instance = await new InstanceManagerTest(adminClient.Instances, adminClient.Users, server.Directory).RunPreInstanceTest(cancellationToken);

					Assert.IsTrue(Directory.Exists(instance.Path));
					var instanceClient = adminClient.Instances.CreateClient(instance);
					Assert.IsTrue(Directory.Exists(instanceClient.Metadata.Path));

					var instanceTests = FailFast(new InstanceTest(instanceClient, adminClient.Instances).RunTests(cancellationToken));

					await Task.WhenAll(rootTest, adminTest, instanceTests, usersTest);

					await adminClient.Administration.Restart(cancellationToken);
				}

				await Task.WhenAny(serverTask, Task.Delay(30000, cancellationToken));
				Assert.IsTrue(serverTask.IsCompleted);

				// http bind test https://github.com/tgstation/tgstation-server/issues/1065
				using (var blockingSocket = new Socket(SocketType.Stream, ProtocolType.Tcp))
				{
					blockingSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ExclusiveAddressUse, true);
					blockingSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, false);
					blockingSocket.Bind(new IPEndPoint(IPAddress.Any, server.Url.Port));
					try
					{
						await server.Run(cancellationToken);
						Assert.Fail("Expected server task to end with a SocketException");
					}
					catch (SocketException ex)
					{
						Assert.AreEqual(ex.SocketErrorCode, SocketError.AddressAlreadyInUse);
					}
				}

				await Task.WhenAny(serverTask, Task.Delay(30000, cancellationToken));
				Assert.IsTrue(serverTask.IsCompleted);

				var preStartupTime = DateTimeOffset.Now;

				serverTask = server.Run(cancellationToken);
				using (var adminClient = await CreateAdminClient(server.Url, cancellationToken))
				{
					var instanceClient = adminClient.Instances.CreateClient(instance);

					var jobs = await instanceClient.Jobs.ListActive(cancellationToken);
					if (!jobs.Any())
					{
						var entities = await instanceClient.Jobs.List(cancellationToken);
						var getTasks = entities
							.Select(e => instanceClient.Jobs.GetId(e, cancellationToken))
							.ToList();

						await Task.WhenAll(getTasks);
						jobs = getTasks
							.Select(x => x.Result)
							.Where(x => x.StartedAt.Value >= preStartupTime)
							.ToList();
					}

					Assert.AreEqual(1, jobs.Count, $"Why are there multiple active jobs? \"{String.Join("\", \"", jobs.Select(x => x.Description))}\"");

					var reattachJob = jobs.Single();
					Assert.IsTrue(reattachJob.StartedAt.Value >= preStartupTime);

					await new JobsRequiredTest(instanceClient.Jobs).WaitForJob(reattachJob, 40, false, null, cancellationToken);

					var dd = await instanceClient.DreamDaemon.Read(cancellationToken);
					Assert.AreEqual(WatchdogStatus.Online, dd.Status.Value);

					await instanceClient.DreamDaemon.Shutdown(cancellationToken);
					await instanceClient.DreamDaemon.Update(new DreamDaemon
					{
						AutoStart = true
					}, cancellationToken);

					await adminClient.Administration.Restart(cancellationToken);
				}

				await Task.WhenAny(serverTask, Task.Delay(30000, cancellationToken));
				Assert.IsTrue(serverTask.IsCompleted);

				preStartupTime = DateTimeOffset.Now;
				serverTask = server.Run(cancellationToken);
				using (var adminClient = await CreateAdminClient(server.Url, cancellationToken))
				{
					var instanceClient = adminClient.Instances.CreateClient(instance);

					var jobs = await instanceClient.Jobs.ListActive(cancellationToken);
					if (!jobs.Any())
					{
						var entities = await instanceClient.Jobs.List(cancellationToken);
						var getTasks = entities
							.Select(e => instanceClient.Jobs.GetId(e, cancellationToken))
							.ToList();

						await Task.WhenAll(getTasks);
						jobs = getTasks
							.Select(x => x.Result)
							.Where(x => x.StartedAt.Value > preStartupTime)
							.ToList();
					}

					Assert.AreEqual(1, jobs.Count);

					var launchJob = jobs.Single();
					Assert.IsTrue(launchJob.StartedAt.Value >= preStartupTime);

					await new JobsRequiredTest(instanceClient.Jobs).WaitForJob(launchJob, 40, false, null, cancellationToken);

					var dd = await instanceClient.DreamDaemon.Read(cancellationToken);

					Assert.AreEqual(WatchdogStatus.Online, dd.Status.Value);

					var repoTest = new RepositoryTest(instanceClient.Repository, instanceClient.Jobs).RunPostTest(cancellationToken);
					await new ChatTest(instanceClient.ChatBots, adminClient.Instances, instance).RunPostTest(cancellationToken);
					await repoTest;

					await new InstanceManagerTest(adminClient.Instances, adminClient.Users, server.Directory).RunPostTest(cancellationToken);
				}
			}
			catch(ApiException ex)
			{
				Console.WriteLine($"[{DateTimeOffset.Now}] TEST ERROR: {ex.ErrorCode}: {ex.Message}\n{ex.AdditionalServerData}");
				throw;
			}
			catch (Exception ex)
			{
				Console.WriteLine($"[{DateTimeOffset.Now}] TEST ERROR: {ex}");
				throw;
			}
			finally
			{
				tooLateForSoftTimeout = true;
				serverCts.Cancel();
				try
				{
					await serverTask.WithToken(hardTimeoutCancellationToken).ConfigureAwait(false);
				}
				catch (OperationCanceledException) { }

				TerminateAllDDs();

				hardTimeoutCancellationToken.ThrowIfCancellationRequested();
			}
		}

		[TestMethod]
		public async Task TestScriptExecution()
		{
			var platformIdentifier = new PlatformIdentifier();
			var processExecutor = new ProcessExecutor(
				Mock.Of<IProcessFeatures>(),
				Mock.Of<ILogger<ProcessExecutor>>(),
				LoggerFactory.Create(x => { }));

			using var process = processExecutor.LaunchProcess("test." + platformIdentifier.ScriptFileExtension, ".", String.Empty, true, true, true);
			using var cts = new CancellationTokenSource();
			cts.CancelAfter(3000);
			var exitCode = await process.Lifetime.WithToken(cts.Token);

			Assert.AreEqual(0, exitCode);
			Assert.AreEqual(String.Empty, process.GetErrorOutput().Trim());
			Assert.AreEqual("Hello World!", process.GetStandardOutput().Trim());
		}
	}
}
