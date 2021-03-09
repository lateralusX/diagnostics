// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Internal.Common.Utils;
using System.Linq;
using System.Diagnostics;

namespace Microsoft.Diagnostics.Tools.DSProxy
{
    public class DiagnosticServerProxy
    {
        public DiagnosticServerProxy()
        {
        }

        async Task<int> RunProxy(string clientAddress, string serverAddress, CancellationToken token)
        {
            List<ClientServerProxyFactory.ConnectedProxy> runningProxies = new List<ClientServerProxyFactory.ConnectedProxy>();
            var proxyFactory = new ClientServerProxyFactory(clientAddress, serverAddress);

            try
            {
                proxyFactory.Start();
                while (!token.IsCancellationRequested)
                {
                    Task< ClientServerProxyFactory.ConnectedProxy> connectedProxyTask = null;
                    ClientServerProxyFactory.ConnectedProxy connectedProxy = null;

                    try
                    {
                        connectedProxyTask = proxyFactory.ConnectProxyAsync(token);
                        while (await Task.WhenAny(connectedProxyTask, Task.Delay(500)).ConfigureAwait(false) != connectedProxyTask)
                        {
                            // Search list and clean up dead proxy instances before continue waiting on new instances.
                            foreach (var item in runningProxies.ToList())
                            {
                                if (!item.IsRunning)
                                {
                                    runningProxies.Remove(item);
                                    item.Dispose();
                                }
                            }
                        }

                        if (connectedProxyTask.IsCompleted && !connectedProxyTask.IsFaulted)
                        {
                            connectedProxy = connectedProxyTask.Result;
                            connectedProxy.Start();

                            // Add to list of running proxy instances.
                            runningProxies.Add(connectedProxy);
                            connectedProxy = null;
                        }

                        connectedProxyTask.Dispose();
                        connectedProxyTask = null;
                    }
                    catch (Exception)
                    {
                        // TODO: If we get RuntimeConnectTimeoutException (no runtime connected), should that bring down proxy?

                        connectedProxy?.Dispose();
                        connectedProxy = null;

                        connectedProxyTask?.Dispose();
                        connectedProxyTask = null;
                    }
                }
            }
            finally
            {
                foreach (var item in runningProxies)
                    item.Dispose();

                runningProxies.Clear();
                proxyFactory?.Stop();
            }
            return 0;
        }

        public async Task<int> Run(CancellationToken token, String clientAddress, String serverAddress)
        {
            clientAddress = "MyDummyPort";
            serverAddress = "*:9000";

            ManualResetEvent shouldExit = new ManualResetEvent(false);
            token.Register(() => shouldExit.Set());

            CancellationTokenSource cancelProxyTask = new CancellationTokenSource();
            Task proxyTask = new Task(() => {
                try
                {
                    RunProxy(clientAddress, serverAddress, CancellationTokenSource.CreateLinkedTokenSource(token, cancelProxyTask.Token).Token).Wait();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ERROR] {ex.ToString()}");
                }
                finally
                {
                    shouldExit.Set();
                }
            });

            proxyTask.Start();

            while(!shouldExit.WaitOne(250))
            {
                while (true)
                {
                    if (shouldExit.WaitOne(250))
                    {
                        cancelProxyTask.Cancel();
                        proxyTask.Wait();
                        return 0;
                    }
                    if (Console.KeyAvailable)
                    {
                        break;
                    }
                }
                ConsoleKey cmd = Console.ReadKey(true).Key;
                if (cmd == ConsoleKey.Q || cmd == ConsoleKey.C)
                {
                    cancelProxyTask.Cancel();
                    proxyTask.Wait();
                    break;
                }
            }
            return await Task.FromResult(0);
        }
    }
}
