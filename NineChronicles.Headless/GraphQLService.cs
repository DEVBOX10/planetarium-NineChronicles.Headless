using System;
using System.Collections.Generic;
using GraphQL.Server;
using GraphQL.Utilities;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NineChronicles.Headless.GraphTypes;
using NineChronicles.Headless.Middleware;
using NineChronicles.Headless.Properties;
using Serilog;
using NCAction = Libplanet.Action.PolymorphicAction<Nekoyume.Action.ActionBase>;

namespace NineChronicles.Headless
{
    public class GraphQLService
    {
        public const string LocalPolicyKey = "LocalPolicy";

        public const string NoCorsPolicyName = "AllowAllOrigins";

        public const string SecretTokenKey = "secret";

        public const string NoCorsKey = "noCors";

        public const string UseMagicOnionKey = "useMagicOnion";

        public const string MagicOnionTargetKey = "magicOnionTarget";

        private GraphQLNodeServiceProperties GraphQlNodeServiceProperties { get; }

        public GraphQLService(GraphQLNodeServiceProperties properties)
        {
            GraphQlNodeServiceProperties = properties;
        }

        public IHostBuilder Configure(IHostBuilder hostBuilder)
        {
            var listenHost = GraphQlNodeServiceProperties.GraphQLListenHost;
            var listenPort = GraphQlNodeServiceProperties.GraphQLListenPort;

            return hostBuilder.ConfigureWebHostDefaults(builder =>
            {
                builder.UseStartup<GraphQLStartup>();
                builder.ConfigureAppConfiguration(
                    (context, builder) =>
                    {
                        var dictionary = new Dictionary<string, string>();
                        if (GraphQlNodeServiceProperties.SecretToken is { } secretToken)
                        {
                            dictionary[SecretTokenKey] = secretToken;
                        }

                        if (GraphQlNodeServiceProperties.NoCors)
                        {
                            dictionary[NoCorsKey] = string.Empty;
                        }

                        if (GraphQlNodeServiceProperties.UseMagicOnion)
                        {
                            dictionary[UseMagicOnionKey] = string.Empty;
                        }

                        if (GraphQlNodeServiceProperties.HttpOptions is { } options)
                        {
                            dictionary[MagicOnionTargetKey] = options.Target;
                        }

                        builder.AddInMemoryCollection(dictionary);
                    })
                    .ConfigureKestrel(options =>
                    {
                        options.ListenAnyIP((int)listenPort!, listenOptions =>
                        {
                            listenOptions.Protocols = HttpProtocols.Http1AndHttp2;
                        });
                    });
            });
        }

        internal class GraphQLStartup
        {
            public GraphQLStartup(IConfiguration configuration)
            {
                Configuration = configuration;
            }

            public IConfiguration Configuration { get; }

            public void ConfigureServices(IServiceCollection services)
            {
                if (!(Configuration[NoCorsKey] is null))
                {
                    services.AddCors(
                        options =>
                            options.AddPolicy(
                                NoCorsPolicyName,
                                builder =>
                                    builder.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));
                }

                services.AddTransient<LocalAuthenticationMiddleware>();

                services.AddHealthChecks();

                services.AddControllers();
                services.AddGraphQL(
                        (options, provider) =>
                        {
                            options.EnableMetrics = true;
                            options.UnhandledExceptionDelegate = context =>
                            {
                                Console.Error.WriteLine(context.Exception.ToString());
                                Console.Error.WriteLine(context.ErrorMessage);
                            };
                        })
                    .AddSystemTextJson()
                    .AddWebSockets()
                    .AddDataLoader()
                    .AddGraphTypes(typeof(StandaloneSchema))
                    .AddLibplanetExplorer<NCAction>()
                    .AddUserContextBuilder<UserContextBuilder>()
                    .AddGraphQLAuthorization(
                        options => options.AddPolicy(
                            LocalPolicyKey,
                            p =>
                                p.RequireClaim(
                                    "role",
                                    "Admin")));
                services.AddGraphTypes();
            }

            public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
            {
                if (env.IsDevelopment())
                {
                    app.UseDeveloperExceptionPage();
                }

                // Capture requests
                app.UseMiddleware<HttpCaptureMiddleware>();

                app.UseMiddleware<LocalAuthenticationMiddleware>();
                if (Configuration[NoCorsKey] is null)
                {
                    app.UseCors();
                }
                else
                {
                    app.UseCors("AllowAllOrigins");
                }

                app.UseRouting();
                app.UseAuthorization();
                app.UseEndpoints(endpoints =>
                {
                    endpoints.MapControllers();
                    if (!(Configuration[UseMagicOnionKey] is null))
                    {
                        endpoints.MapMagicOnionService();

                        if (Configuration[MagicOnionTargetKey] is { } magicOnionTarget)
                        {
                            var options = new GrpcChannelOptions
                            {
                                Credentials = ChannelCredentials.Insecure,
                                MaxReceiveMessageSize = null,
                            };

                            endpoints.MapMagicOnionHttpGateway("_",
                                app.ApplicationServices.GetService<MagicOnion.Server.MagicOnionServiceDefinition>()!
                                    .MethodHandlers, GrpcChannel.ForAddress($"http://{magicOnionTarget}", options));
                            endpoints.MapMagicOnionSwagger("swagger",
                                app.ApplicationServices.GetService<MagicOnion.Server.MagicOnionServiceDefinition>()!
                                    .MethodHandlers, "/_/");
                        }
                    }
                    endpoints.MapHealthChecks("/health-check");
                });

                // WebSocket으로 운영합니다.
                app.UseWebSockets();
                app.UseGraphQLWebSockets<StandaloneSchema>("/graphql");
                app.UseGraphQL<StandaloneSchema>("/graphql");

                // Prints 
                app.UseMiddleware<GraphQLSchemaMiddleware<StandaloneSchema>>("/schema.graphql");

                // /ui/playground 옵션을 통해서 Playground를 사용할 수 있습니다.
                app.UseGraphQLPlayground();
            }
        }
    }
}
