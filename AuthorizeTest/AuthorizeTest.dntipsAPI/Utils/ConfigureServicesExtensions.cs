﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using AuthorizeTest.dntipsAPI.Context;
using AuthorizeTest.dntipsAPI.Models;
using AuthorizeTest.dntipsAPI.Services;
using AuthorizeTest.Shared.Models;
using AuthorizeTest.Shared.Utils;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;

namespace AuthorizeTest.dntipsAPI.Utils

{
    public static class ConfigureServicesExtensions
    {
        public static void AddCustomAntiforgery(this IServiceCollection services)
        {
            services.AddAntiforgery(x => x.HeaderName = "X-XSRF-TOKEN");
            services.AddMvc(options =>
            {
                options.Filters.Add(new AutoValidateAntiforgeryTokenAttribute());
            });
        }

        public static void AddCustomCors(this IServiceCollection services)
        {
            services.AddCors(options =>
            {
                options.AddPolicy("CorsPolicy",
                    builder => builder
                        .WithOrigins("https://localhost:7123") //Note:  The URL must be specified without a trailing slash (/).
                        .AllowAnyMethod()
                        .AllowAnyHeader()
                        .SetIsOriginAllowed((host) => true)
                        .AllowCredentials());
            });
        }

        public static void AddCustomJwtBearer(this IServiceCollection services, IConfiguration configuration)
        {

            // Only needed for custom roles.
            services.AddAuthorization(options =>
            {
                 options.AddAppPolicies();
                //options.AddPolicy(ConstantRoles.Admin, policy => policy.RequireRole(ConstantRoles.Admin));
                //options.AddPolicy(ConstantRoles.User, policy => policy.RequireRole(ConstantRoles.User));
                //options.AddPolicy(ConstantRoles.Editor, policy => policy.RequireRole(ConstantRoles.Editor));
            });

            // Needed for jwt auth.
            services
                .AddAuthentication(options =>
                {
                    options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
                    options.DefaultSignInScheme = JwtBearerDefaults.AuthenticationScheme;
                    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
                })
                .AddJwtBearer(cfg =>
                {
                    cfg.RequireHttpsMetadata = false;
                    cfg.SaveToken = true;
                    cfg.TokenValidationParameters = new TokenValidationParameters
                    {
                        ValidIssuer = configuration["BearerTokens:Issuer"], // site that makes the token
                        ValidateIssuer = false, // TODO: change this to avoid forwarding attacks
                        ValidAudience = configuration["BearerTokens:Audience"], // site that consumes the token
                        ValidateAudience = false, // TODO: change this to avoid forwarding attacks
                        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(configuration["BearerTokens:Key"])),
                        ValidateIssuerSigningKey = true, // verify signature to avoid tampering
                        ValidateLifetime = true, // validate the expiration
                        ClockSkew = TimeSpan.Zero // tolerance for the expiration date
                    };
                    cfg.Events = new JwtBearerEvents
                    {
                        OnAuthenticationFailed = context =>
                        {
                            var logger = context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(JwtBearerEvents));
                            logger.LogError("Authentication failed:{0}.", context.Exception);
                            return Task.CompletedTask;
                        },
                        OnTokenValidated = context =>
                        {
                            var tokenValidatorService = context.HttpContext.RequestServices.GetRequiredService<ITokenValidatorService>();
                            return tokenValidatorService.ValidateAsync(context);
                        },
                        OnMessageReceived = context =>
                        {
                            return Task.CompletedTask;
                        },
                        OnChallenge = context =>
                        {
                            var logger = context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(JwtBearerEvents));
                            logger.LogError("OnChallenge error:{0}{1}Error Description:{2}.", context.Error,Environment.NewLine, context.ErrorDescription);
                            return Task.CompletedTask;
                        }
                    };
                });
        }

        public static void AddCustomDbContext(this IServiceCollection services, IConfiguration configuration, Assembly startupAssembly)
        {
            string projectDir = ServerPath.GetProjectPath(startupAssembly);
            var connectionString = configuration.GetConnectionString("DefaultConnection")
                                                .Replace("|DataDirectory|", Path.Combine(projectDir, "wwwroot", "app_data"));
            services.AddDbContext<ApplicationDbContext>(options =>
            {
                options.UseSqlServer(connectionString,
                        serverDbContextOptionsBuilder =>
                        {
                            var minutes = (int)TimeSpan.FromMinutes(3).TotalSeconds;
                            serverDbContextOptionsBuilder.CommandTimeout(minutes);
                            serverDbContextOptionsBuilder.EnableRetryOnFailure();
                        });
            });
        }

        public static void AddCustomServices(this IServiceCollection services)
        {
            services.AddSingleton<IHttpContextAccessor, HttpContextAccessor>();
            services.AddScoped<IAntiForgeryCookieService, AntiForgeryCookieService>();
            services.AddScoped<IUnitOfWork, ApplicationDbContext>();
            services.AddScoped<IUsersService, UsersService>();
            services.AddScoped<IRolesService, RolesService>();
            services.AddSingleton<ISecurityService, SecurityService>();
            services.AddScoped<IDbInitializerService, DbInitializerService>();
            services.AddScoped<ITokenStoreService, TokenStoreService>();
            services.AddScoped<ITokenValidatorService, TokenValidatorService>();
            services.AddScoped<ITokenFactoryService, TokenFactoryService>();
        }

        public static void AddCustomOptions(this IServiceCollection services, IConfiguration configuration)
        {
            services.AddOptions<BearerTokensOptions>()
                                .Bind(configuration.GetSection("BearerTokens"))
                                .Validate(bearerTokens =>
                                {
                                    return bearerTokens.AccessTokenExpirationMinutes < bearerTokens.RefreshTokenExpirationMinutes;
                                }, "RefreshTokenExpirationMinutes is less than AccessTokenExpirationMinutes. Obtaining new tokens using the refresh token should happen only if the access token has expired.");
            services.AddOptions<ApiSettings>()
                    .Bind(configuration.GetSection("ApiSettings"));
        }

        public static void UseCustomSwagger(this IApplicationBuilder app)
        {
            app.UseSwagger();
            app.UseSwaggerUI(setupAction =>
            {
                setupAction.SwaggerEndpoint(
                    url: "/swagger/LibraryOpenAPISpecification/swagger.json",
                    name: "Library API");
                //setupAction.RoutePrefix = ""; --> To be able to access it from this URL: https://localhost:5001/swagger/index.html

                setupAction.DefaultModelExpandDepth(2);
                setupAction.DefaultModelRendering(Swashbuckle.AspNetCore.SwaggerUI.ModelRendering.Model);
                setupAction.DocExpansion(Swashbuckle.AspNetCore.SwaggerUI.DocExpansion.None);
                setupAction.EnableDeepLinking();
                setupAction.DisplayOperationId();
            });
        }

        public static void AddCustomSwagger(this IServiceCollection services)
        {
            services.AddSwaggerGen(setupAction =>
            {
                setupAction.SwaggerDoc(
                   name: "LibraryOpenAPISpecification",
                   info: new Microsoft.OpenApi.Models.OpenApiInfo()
                   {
                       Title = "Library API",
                       Version = "1",
                       Description = "Through this API you can access the site's capabilities.",
                       Contact = new Microsoft.OpenApi.Models.OpenApiContact()
                       {
                           Email = "name@site.com",
                           Name = "DNT",
                           Url = new Uri("http://www.dotnettips.info")
                       },
                       License = new Microsoft.OpenApi.Models.OpenApiLicense()
                       {
                           Name = "MIT License",
                           Url = new Uri("https://opensource.org/licenses/MIT")
                       }
                   });

                var xmlFiles = Directory.GetFiles(AppContext.BaseDirectory, "*.xml", SearchOption.TopDirectoryOnly).ToList();
                xmlFiles.ForEach(xmlFile => setupAction.IncludeXmlComments(xmlFile));

                setupAction.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
                {
                    Type = SecuritySchemeType.ApiKey,
                    Description = "Put **_ONLY_** your JWT Bearer token on textbox below!",
                    In = ParameterLocation.Header,
                    Name = "Authorization",
                    Scheme = "bearer",
                    BearerFormat = "JWT"
                });

                setupAction.AddSecurityRequirement(new OpenApiSecurityRequirement
                {
                    {
                        new OpenApiSecurityScheme
                        {
                            Reference = new OpenApiReference
                            {
                                Type = ReferenceType.SecurityScheme,
                                Id = "Bearer"
                            }
                        },
                        new List<string>()
                    }
                });
            });
        }
    }
}
