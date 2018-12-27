﻿using Autofac;
using Autofac.Extensions.DependencyInjection;
using Autofac.Extras.DynamicProxy;
using AutoMapper;
using log4net;
using log4net.Config;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Swashbuckle.AspNetCore.Swagger;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Unicode;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Titan.Blog.Infrastructure.AOP;
using Titan.Blog.Infrastructure.Auth.Policys;
using Titan.Blog.Infrastructure.HttpExtenions;
using Titan.Blog.Infrastructure.Log;
using Titan.Blog.Infrastructure.Redis;
using Titan.Blog.WebAPP.Filter;
using Titan.Blog.WebAPP.Swagger;
using Titan.Model.DataModel;
using Titan.Blog.AppService.DomainService;

namespace Titan.Blog.WebAPP
{
    public class Startup
    {
        #region 仓储 --Log4Net、.Net Core Configuration
        /// <summary>
        /// log4net 仓储库
        /// </summary>
        //public static ILoggerRepository Repository { get; set; }
        public IConfiguration Configuration { get; }

        #endregion

        #region .Net Core 启动
        public Startup(IConfiguration configuration)
        {
            Configuration = configuration;
            //log4net
            LogHelper.Repository = LogManager.CreateRepository("Titan.Blog.WebAPP");//创建log4net仓储，并丢给公共库
            //指定配置文件
            XmlConfigurator.Configure(LogHelper.Repository, new FileInfo("log4net.config"));//重定向log4net仓储配置文件
        }
        #endregion

        #region .Net Core 配置服务注入
        // This method gets called by the runtime. Use this method to add services to the container.
        public IServiceProvider ConfigureServices(IServiceCollection services)
        {
            #region EF Core
            //注入上下文对象
            services.AddDbContext<ModelBaseContext>(options =>
                options.UseSqlServer(Configuration.GetConnectionString("DefaultConnection")));
            #endregion

            #region 全局异常捕获
            services.AddMvc().SetCompatibilityVersion(CompatibilityVersion.Version_2_2);
            #endregion

            #region Redis缓存
            services.AddScoped<IRedisCacheManager, RedisCacheManager>();
            #endregion

            #region Log4Net日志
            services.AddSingleton<ILoggerHelper, LogHelper>();
            #endregion

            #region AutoMapper模型映射
            services.AddAutoMapper(typeof(Startup));
            #endregion

            #region CORS全局跨域
            //跨域第二种方法，声明策略，记得下边app中配置
            services.AddCors(c =>
            {
                //↓↓↓↓↓↓↓注意正式环境不要使用这种全开放的处理↓↓↓↓↓↓↓↓↓↓
                c.AddPolicy("AllRequests", policy =>
                {
                    policy
                    .AllowAnyOrigin()//允许任何源
                    .AllowAnyMethod()//允许任何方式
                    .AllowAnyHeader()//允许任何头
                    .AllowCredentials();//允许cookie
                });
                //↑↑↑↑↑↑↑注意正式环境不要使用这种全开放的处理↑↑↑↑↑↑↑↑↑↑

                //一般采用这种方法
                c.AddPolicy("LimitRequests", policy =>
                {
                    policy
                    .WithOrigins("http://127.0.0.1:1818", "http://localhost:8080", "http://localhost:8021", "http://localhost:8081", "http://localhost:1818")//支持多个域名端口，注意端口号后不要带/斜杆：比如localhost:8000/，是错的
                    .AllowAnyHeader()//Ensures that the policy allows any header.
                    .AllowAnyMethod();
                });
            });

            //跨域第一种办法，注意下边 Configure 中进行配置
            //services.AddCors();
            #endregion

            #region Swagger UI Service API文档服务
            var basePath = Microsoft.DotNet.PlatformAbstractions.ApplicationEnvironment.ApplicationBasePath;
            services.AddSwaggerGen(c =>
            {
                //c.SwaggerDoc("v1", new Info
                //{
                //    Version = "v0.1.0",
                //    Title = "Blog.Core API",
                //    Description = "框架说明文档",
                //    TermsOfService = "None",
                //    Contact = new Swashbuckle.AspNetCore.Swagger.Contact { Name = "Blog.Core", Email = "Blog.Core@xxx.com", Url = "https://www.jianshu.com/u/94102b59cc2a" }
                //});

                //遍历出全部的版本，做文档信息展示
                typeof(CustomApiVersion.ApiVersions).GetEnumNames().ToList().ForEach(version =>
                {
                    c.SwaggerDoc(version, new Info
                    {
                        // {ApiName} 定义成全局变量，方便修改
                        Version = version,
                        Title = $"{(Configuration.GetSection("Swagger"))["ProjectName"]} 接口文档",
                        Description = $"{(Configuration.GetSection("Swagger"))["ProjectName"]} HTTP API " + version,
                        TermsOfService = "None",
                        Contact = new Contact { Name = "Titan.Blog.WebAPP", Email = "1454055505@qq.com", Url = "https://blog.csdn.net/black_bad1993" }
                    });
                });
                var xmlPath1 = Path.Combine(basePath, "Titan.Blog.WebAPP.xml");//这个就是刚刚配置的xml文件名
                c.IncludeXmlComments(xmlPath1, true);//默认的第二个参数是false，这个是controller的注释，记得修改
                var xmlPath2 = Path.Combine(basePath, "Titan.Blog.AppService.xml");//这个就是Model层的xml文件名
                c.IncludeXmlComments(xmlPath2);
                var xmlPath3 = Path.Combine(basePath, "Titan.Blog.Model.xml");//这个就是Model层的xml文件名
                c.IncludeXmlComments(xmlPath3);

                #region Token绑定到ConfigureServices
                //添加header验证信息
                //c.OperationFilter<SwaggerHeader>();
                // 发行人
                var issuerName = (Configuration.GetSection("Audience"))["Issuer"];
                var security = new Dictionary<string, IEnumerable<string>> { { issuerName, new string[] { } }, };
                c.AddSecurityRequirement(security);

                //方案名称“Blog.WebAPP”可自定义，上下一致即可
                c.AddSecurityDefinition(issuerName, new ApiKeyScheme
                {
                    Description = "JWT授权(数据将在请求头中进行传输) 直接在下框中输入Bearer token（注意两者之间是一个空格）\"",
                    Name = "Authorization",//jwt默认的参数名称
                    In = "header",//jwt默认存放Authorization信息的位置(请求头中)
                    Type = "apiKey"
                });
                #endregion

                #region Swagger文件上传配置
                c.OperationFilter<SwaggerUploadFileFilter>();
                #endregion

                #region Swagger文档过滤
                c.DocumentFilter<RemoveBogusDefinitionsDocumentFilter>();
                #endregion
            });
            #endregion

            #region JWT Token Service Token授权认证服务
            //读取配置文件
            var audienceConfig = Configuration.GetSection("Audience");
            var symmetricKeyAsBase64 = audienceConfig["Secret"];
            var keyByteArray = Encoding.ASCII.GetBytes(symmetricKeyAsBase64);
            var signingKey = new SymmetricSecurityKey(keyByteArray);

            // 令牌验证参数
            var tokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = signingKey,
                ValidateIssuer = true,
                ValidIssuer = audienceConfig["Issuer"],//发行人
                ValidateAudience = true,
                ValidAudience = audienceConfig["Audience"],//订阅人
                ValidateLifetime = true,
                ClockSkew = TimeSpan.Zero,
                RequireExpirationTime = true,

            };
            var signingCredentials = new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256);

            // 注意使用RESTful风格的接口会更好，因为只需要写一个Url即可，比如：/api/values 代表了Get Post Put Delete等多个。
            // 如果想写死，可以直接在这里写。
            //var permission = new List<Permission> {
            //                  new Permission {  Url="/api/values", Role="Admin"},
            //                  new Permission {  Url="/api/values", Role="System"},
            //                  new Permission {  Url="/api/claims", Role="Admin"},
            //              };

            // 如果要数据库动态绑定，这里先留个空，后边处理器里动态赋值
            var permission = new List<Permission>();

            // 角色与接口的权限要求参数
            var permissionRequirement = new PermissionRequirement(
                "/api/denied", // 拒绝授权的跳转地址（目前无用）
                permission,
                ClaimTypes.Role, //基于角色的授权
                audienceConfig["Issuer"], //发行人
                audienceConfig["Audience"], //听众
                signingCredentials, //签名凭据
                expiration: TimeSpan.FromSeconds(50) //接口的过期时间
            );

            //加载角色策略 一个策略对应多个角色，一个角色可以对应多个策略，一个人可以有多个角色
            services.AddAuthorization(options =>
            {
                options.AddPolicy("Client",
                    policy => policy.RequireRole("Client").Build());
                options.AddPolicy("Admin",
                    policy => policy.RequireRole("Admin").Build());
                options.AddPolicy("SystemOrAdmin",
                    policy => policy.RequireRole("Admin", "System"));

                // 自定义权限要求
                options.AddPolicy("Permission",
                         policy => policy.Requirements.Add(permissionRequirement));
            })
            .AddAuthentication(x =>
            {
                x.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
                x.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
            })
            .AddJwtBearer(o =>
            {
                o.TokenValidationParameters = tokenValidationParameters;
                //o.TokenValidationParameters = new TokenValidationParameters
                //{
                //    ValidateIssuer = true,//是否验证Issuer
                //    ValidateAudience = true,//是否验证Audience 
                //    ValidateIssuerSigningKey = true,//是否验证IssuerSigningKey 
                //    ValidIssuer = "Blog.Core",
                //    ValidAudience = "wr",
                //    ValidateLifetime = true,//是否验证超时  当设置exp和nbf时有效 同时启用ClockSkew 
                //    IssuerSigningKey = new SymmetricSecurityKey(Encoding.ASCII.GetBytes(JwtHelper.secretKey)),
                //    //注意这是缓冲过期时间，总的有效时间等于这个时间加上jwt的过期时间
                //    ClockSkew = TimeSpan.FromSeconds(30)

                //};
            });
            //自定义授权策略拦截器 -- 处理自定义策略的角色访问权限
            services.AddSingleton<IAuthorizationHandler, PermissionHandler>();
            services.AddSingleton(permissionRequirement);
            #endregion

            #region HttpContext上下文注入
            services.AddSingleton<IHttpContextAccessor, HttpContextAccessor>();//httpcontext上下文
            #endregion

            #region AutoFac
            //实例化 AutoFac  容器   
            var builder = new ContainerBuilder();
            //注册要通过反射创建的组件
            //builder.RegisterType<AdvertisementServices>().As<IAdvertisementServices>();
            builder.RegisterType<BlogCacheAOP>();//可以直接替换其他拦截器
            //builder.RegisterType<AuthorDomainSvc>();//可以直接替换其他拦截器
            //var assemblysServices1 = Assembly.Load("Blog.Core.Services");


            // ※※★※※ 如果你是第一次下载项目，请先F6编译，然后再F5执行，※※★※※
            // ※※★※※ 因为解耦，bin文件夹没有以下两个dll文件，会报错，所以先编译生成这两个dll ※※★※※



            var servicesDllFile = Path.Combine(basePath, "Titan.Blog.AppService.dll");//获取项目绝对路径
            var assemblysServices = Assembly.LoadFile(servicesDllFile);//直接采用加载文件的方法
            builder.RegisterAssemblyTypes(assemblysServices).AsImplementedInterfaces();//指定已扫描程序集中的类型注册为提供所有其实现的接口。
            builder.RegisterAssemblyTypes(assemblysServices)
                     .AsImplementedInterfaces()
                     .InstancePerLifetimeScope()
                     .EnableInterfaceInterceptors()//引用Autofac.Extras.DynamicProxy;
                     .InterceptedBy(typeof(BlogCacheAOP));//允许将拦截器服务的列表分配给注册。可以直接替换其他拦截器

            var infrastructureDllFile = Path.Combine(basePath, "Titan.Blog.Infrastructure.dll");//获取项目绝对路径
            var assemblysInfrastructure = Assembly.LoadFile(infrastructureDllFile);//直接采用加载文件的方法
            builder.RegisterAssemblyTypes(assemblysInfrastructure).AsImplementedInterfaces();//指定已扫描程序集中的类型注册为提供所有其实现的接口。

            var modelDllFile = Path.Combine(basePath, "Titan.Blog.Model.dll");//获取项目绝对路径
            var assemblysModel = Assembly.LoadFile(modelDllFile);//直接采用加载文件的方法
            builder.RegisterAssemblyTypes(assemblysModel).AsImplementedInterfaces();//指定已扫描程序集中的类型注册为提供所有其实现的接口。

            var repositoryDllFile = Path.Combine(basePath, "Titan.Blog.Repository.dll");
            var assemblysRepository = Assembly.LoadFile(repositoryDllFile);
            builder.RegisterAssemblyTypes(assemblysRepository).AsImplementedInterfaces();

            //将services填充到Autofac容器生成器中
            builder.Populate(services);
           
            //使用已进行的组件登记创建新容器
            var applicationContainer = builder.Build();
            #endregion
            
            return new AutofacServiceProvider(applicationContainer);//第三方IOC接管 core内置DI容器
        }
        #endregion

        #region .Net Core 配置
        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        public void Configure(IApplicationBuilder app, IHostingEnvironment env, IServiceProvider svp)
        {
            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }
            else
            {
                // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
                //app.UseHsts();//hsts是http转https过程中用到的一种技术，防止劫持HTTP请求
            }

            #region 全局错误拦截器配置
            app.UseErrorHandling();
            #endregion

            #region Swagger配置
            app.UseSwagger();
            app.UseSwaggerUI(c =>
            {
                //之前是写死的
                //c.SwaggerEndpoint("/swagger/v1/swagger.json", "ApiHelp V1");
                //c.RoutePrefix = "";//路径配置，设置为空，表示直接在根域名（localhost:8001）访问该文件,注意localhost:8001/swagger是访问不到的，去launchSettings.json把launchUrl去掉

                //根据版本名称倒序 遍历展示
                typeof(CustomApiVersion.ApiVersions).GetEnumNames().OrderByDescending(e => e).ToList().ForEach(version =>
                {
                    c.SwaggerEndpoint($"/swagger/{version}/swagger.json", $"{(Configuration.GetSection("Swagger"))["" + "ProjectName" + ""]} {version}");
                });
            });
            #endregion

            app.UseStaticHttpContext();

            #region 认证配置
            //app.UseMiddleware<JwtTokenAuth>();//注意此授权方法已经放弃，请使用下边的官方验证方法。但是如果你还想传User的全局变量，还是可以继续使用中间件
            app.UseAuthentication();
            #endregion

            #region Cors跨域配置
            //跨域第二种方法，使用策略，详细策略信息在ConfigureService中
            app.UseCors("LimitRequests");//将 CORS 中间件添加到 web 应用程序管线中, 以允许跨域请求。
                                         //跨域第一种版本，请要ConfigureService中配置服务 services.AddCors();
                                         //    app.UseCors(options => options.WithOrigins("http://localhost:8021").AllowAnyHeader()
                                         //.AllowAnyMethod());
            #endregion

            app.UseStatusCodePages();//把错误码返回前台，比如是404
            //app.UseHttpsRedirection();//将Http重定向Https
            app.UseMvc();
        }
        #endregion
    }
}
