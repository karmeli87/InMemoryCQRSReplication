using System;
using System.IO;
using System.Threading.Tasks;
using Akka.Actor;
using Akka.Bootstrap.Docker;
using Akka.Cluster.Sharding;
using Akka.Cluster.Tools.PublishSubscribe;
using Akka.Cluster.Tools.Singleton;
using Akka.Configuration;
using Akka.CQRS.Infrastructure;
using Akka.CQRS.Infrastructure.Ops;
using Akka.CQRS.Pricing.Actors;
using Akka.CQRS.Pricing.Cli;
using Akka.Hosting;
using Akka.Persistence.Query;
using Akka.Persistence.RavenDb;
using Akka.Persistence.RavenDb.Query;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Petabridge.Cmd.Cluster;
using Petabridge.Cmd.Cluster.Sharding;
using Petabridge.Cmd.Host;
using Petabridge.Cmd.Remote;
using static Akka.CQRS.Infrastructure.RavenDbHoconHelper;

namespace Akka.CQRS.Pricing.Service
{
    public static class Program
    {
        public static async Task<int> Main(string[] args)
        {
            var url = Environment.GetEnvironmentVariable("RAVENDB_URL")?.Trim();
            if (string.IsNullOrEmpty(url))
            {
                Console.WriteLine("ERROR! RavenDB url not provided. Can't start.");
                return -1;
            }
            Console.WriteLine($"Connecting to RavenDB server at {url}");

            var database = Environment.GetEnvironmentVariable("RAVENDB_NAME")?.Trim();
            if (string.IsNullOrEmpty(database))
            {
                Console.WriteLine("ERROR! RavenDB database name not provided. Can't start.");
                return -1;
            }
            Console.WriteLine($"Connecting to RavenDB database {database}");

            // Need to wait for the RavenDB server to spin up
          //  await Task.Delay(TimeSpan.FromSeconds(15));
            
            var config = await File.ReadAllTextAsync("app.conf");
            using var host = new HostBuilder()
            .ConfigureServices((hostContext, services) =>
            {

                services.AddAkka("AkkaPricing", options =>
                {
                    // Add HOCON configuration from Docker
                    var conf = ConfigurationFactory.ParseString(config)                 
                    .WithFallback(GetRavenDbHocon(url, database))                
                    .WithFallback(OpsConfig.GetOpsConfig())                 
                    .WithFallback(ClusterSharding.DefaultConfig())                 
                    .WithFallback(DistributedPubSub.DefaultConfig())                 
                    .WithFallback(RavenDbPersistence.DefaultConfiguration());
                    options.AddHocon(conf.BootstrapFromDocker(), HoconAddMode.Prepend)
                        .ConfigureLoggers(setup =>
                        {
                            setup.LogLevel = Event.LogLevel.WarningLevel;
                        })
                    .WithActors((system, registry) =>
                    {
                        var priceViewMaster = system.ActorOf(Props.Create(() => new PriceViewMaster()), "prices");
                        registry.Register<PriceViewMaster>(priceViewMaster);
                        // used to seed pricing data
                        var readJournal = system.ReadJournalFor<RavenDbReadJournal>(RavenDbReadJournal.Identifier);
                        Cluster.Cluster.Get(system).RegisterOnMemberUp(() =>
                        {
                            var sharding = ClusterSharding.Get(system);

                            var shardRegion = sharding.Start("priceAggregator",
                                s => Props.Create(() => new MatchAggregator(s, readJournal)),
                                ClusterShardingSettings.Create(system),
                                new StockShardMsgRouter());

                            // used to seed pricing data
                            var singleton = ClusterSingletonManager.Props(
                                Props.Create(() => new PriceInitiatorActor(readJournal, shardRegion)),
                                ClusterSingletonManagerSettings.Create(
                                    system.Settings.Config.GetConfig("akka.cluster.price-singleton")));

                            // start the creation of the pricing views
                            priceViewMaster.Tell(new PriceViewMaster.BeginTrackPrices(shardRegion));
                        });
                        
                    })
                    .AddPetabridgeCmd(cmd =>
                    {
                        void RegisterPalette(CommandPaletteHandler h)
                        {
                            if (cmd.RegisterCommandPalette(h))
                            {
                                Console.WriteLine("Petabridge.Cmd - Registered {0}", h.Palette.ModuleName);
                            }
                            else
                            {
                                Console.WriteLine("Petabridge.Cmd - DID NOT REGISTER {0}", h.Palette.ModuleName);
                            }
                        }
                        
                        var actorSystem = cmd.Sys;
                        var actorRegistry = ActorRegistry.For(actorSystem);
                        var priceViewMaster = actorRegistry.Get<PriceViewMaster>();

                        RegisterPalette(ClusterCommands.Instance);
                        RegisterPalette(new RemoteCommands());
                        RegisterPalette(ClusterShardingCommands.Instance);
                        RegisterPalette(new PriceCommands(priceViewMaster));
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
            return 0;
        }
    }
}
