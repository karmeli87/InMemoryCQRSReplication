using System;
using System.IO;
using System.Threading.Tasks;
using Akka.Bootstrap.Docker;
using Akka.Cluster.Sharding;
using Akka.Cluster.Tools.PublishSubscribe;
using Akka.Configuration;
using Akka.CQRS.Infrastructure.Ops;
using Akka.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Petabridge.Cmd.Cluster;
using Petabridge.Cmd.Cluster.Sharding;
using Petabridge.Cmd.Host;
using Petabridge.Cmd.Remote;
using static Akka.CQRS.Infrastructure.RavenDbHoconHelper;
using Akka.CQRS.TradeProcessor.Actors;
using Akka.CQRS.Infrastructure;
using Akka.Persistence.RavenDb;

namespace Akka.CQRS.TradeProcessor.Service
{
    public static class Program
    {
        public static async Task<int> Main(string[] args)
        {
            var url = "http://localhost:8080";//Environment.GetEnvironmentVariable("RAVENDB_URL")?.Trim();
            if (string.IsNullOrEmpty(url))
            {
                Console.WriteLine("ERROR! RavenDB url not provided. Can't start.");
                return -1;
            }
            Console.WriteLine($"Connecting to RavenDB server at {url}");

            var database = "TEST";//Environment.GetEnvironmentVariable("RAVENDB_NAME")?.Trim();
            if (string.IsNullOrEmpty(database))
            {
                Console.WriteLine("ERROR! RavenDB database name not provided. Can't start.");
                return -1;
            }
            Console.WriteLine($"Connecting to RavenDB database {database}");

            // Need to wait for the SQL server to spin up
            // await Task.Delay(TimeSpan.FromSeconds(15));

            var config = await File.ReadAllTextAsync("app.conf");

            using var host = new HostBuilder()
            .ConfigureServices((hostContext, services) =>
            {
                
                services.AddAkka("AkkaTrader", options =>
                {
                    // Add HOCON configuration from Docker
                    var conf = ConfigurationFactory.ParseString(config)
                        .WithFallback(GetRavenDbHocon(url, database))
                        .WithFallback(OpsConfig.GetOpsConfig())
                        .WithFallback(ClusterSharding.DefaultConfig())
                        .WithFallback(DistributedPubSub.DefaultConfig())
                        .WithFallback(RavenDbPersistence.DefaultConfiguration());
                     options.AddHocon(conf/*.BootstrapFromDocker()*/, HoconAddMode.Prepend)
                     .WithActors((system, registry) =>
                     {
                         Cluster.Cluster.Get(system).RegisterOnMemberUp(() =>
                         {
                             var sharding = ClusterSharding.Get(system);

                             var shardRegion = sharding.Start("orderBook", s => OrderBookActor.PropsFor(s), ClusterShardingSettings.Create(system),
                                 new StockShardMsgRouter());
                         });
                     })
                    .AddPetabridgeCmd(cmd =>
                    {
                        Console.WriteLine("   PetabridgeCmd Added");
                        cmd.RegisterCommandPalette(ClusterCommands.Instance);
                        cmd.RegisterCommandPalette(ClusterShardingCommands.Instance);
                        cmd.RegisterCommandPalette(new RemoteCommands());
                        cmd.Start();
                    });
                    
                });
            })
            .ConfigureLogging((hostContext, configLogging) =>
            {
                configLogging.AddConsole();
            })
            .UseConsoleLifetime()
            .Build();
            await host.RunAsync();
            Console.ReadLine();
            return 0;
        }
    }
}
