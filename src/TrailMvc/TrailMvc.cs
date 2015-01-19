using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.AspNet.Builder;
using Microsoft.AspNet.Http;
using System.Linq;

namespace TrailMvc
{
    public delegate Task TrailContinuationDelegate();

    public class TrailContext
    {
        public ICustomServiceProvider ServiceProviderProperty { get; }
        public HttpContext HttpContext { get; }
        public HttpRequest Request { get; }
        public HttpResponse Response { get; }
        public TrailContinuationDelegate Continue { get; set; }
        public TrailAction Action { get; }

        public TrailContext(ICustomServiceProvider serviceProvider, HttpContext context, TrailAction action)
        {
            ServiceProviderProperty = serviceProvider;
            HttpContext = context;
            Request = context.Request;
            Response = context.Response;
            Action = action;
        }

        // todo add ExitMvc method
        // public Task ExitMvc()
    }

    public static class TrailMvcExtensions
    {
        public static TrailMvc UseTrailMvc(
            this IApplicationBuilder app,
            Func<HttpContext, ICustomServiceProvider> providerFactory)
        {
            var mvc = new TrailMvc(providerFactory);
            app.Use(mvc.Middleware);
            return mvc;
        }
    }

    public delegate void ApplyTrailControllersDelegate(TrailMvc mvc);

    public class TrailMvc
    {
        public const string RegexRoutePrefix = "regex:";
        public const int DefaultRoutePriority = 1000;

        public static Dictionary<Type, TrailParamTypeConverter> ParamTypeConverters
        { get; set; }
        = new Dictionary
            <Type, TrailParamTypeConverter>
        {
            [typeof(string)] = new TrailParamTypeConverter(@"[^/]+", s => s),
            [typeof(sbyte)] = new TrailParamTypeConverter(@"-?\d{1,3}", s => sbyte.Parse(s)),
            [typeof(byte)] = new TrailParamTypeConverter(@"\d{1,1}", s => byte.Parse(s)),
            [typeof(short)] = new TrailParamTypeConverter(@"-?\d{1,5}", s => int.Parse(s)),
            [typeof(ushort)] = new TrailParamTypeConverter(@"\d{1,5}", s => uint.Parse(s)),
            [typeof(int)] = new TrailParamTypeConverter(@"-?\d{1,10}", s => int.Parse(s)),
            [typeof(uint)] = new TrailParamTypeConverter(@"\d{1,10}", s => uint.Parse(s)),
            [typeof(long)] = new TrailParamTypeConverter(@"-?\d{1,19}", s => long.Parse(s)),
            [typeof(ulong)] = new TrailParamTypeConverter(@"\d{1,20}", s => ulong.Parse(s)),
        };

        public IEnumerable<string> HttpMethods => _validHttpMethods.AsEnumerable();

        private static readonly HashSet<string> _validHttpMethods = new HashSet<string> {
            "GET",
            "POST",
            "PUT",
            "HEAD",
            "DELETE",
            "OPTIONS",
            "TRACE",
            "COPY",
            "LOCK",
            "MKCOL",
            "MOVE",
            "PURGE",
            "PROPFIND",
            "PROPPATCH",
            "UNLOCK",
            "REPORT",
            "MKACTIVITY",
            "CHECKOUT",
            "MERGE",
            "M-SEARCH",
            "NOTIFY",
            "SUBSCRIBE",
            "UNSUBSCRIBE",
            "PATCH",
            "SEARCH",
            "CONNECT"
        };

        private RequestDelegate _next;
        private TrailAction[] _actions = new TrailAction[0];
        private List<TrailAction> _pendingActions;
        private readonly object _pendingActionsLock = new object();

        public Func<HttpContext, ICustomServiceProvider> ProviderFactory { get; }

        public TrailMvc(Func<HttpContext, ICustomServiceProvider> providerFactory, ApplyTrailControllersDelegate applyControllers = null)
        {
            ProviderFactory = providerFactory;

            if (applyControllers != null)
                applyControllers(this);
            else
                AutoApplyControllers();
        }

        public RequestDelegate Middleware(RequestDelegate next)
        {
            _next = next;
            return DispatchAsync;
        }

        public async Task DispatchAsync(HttpContext httpContext)
        {
            if (_pendingActions != null)
                SetupPendingActions();

            var actions = _actions; // copy actions so that we don't end up seeing an inconsistent state due to added actions
            var method = httpContext.Request.Method;
            var path = httpContext.Request.Path.ToString();
            foreach (var a in actions)
            {
                var match = a.Match(method, path);
                if (match != null)
                {
                    var serviceProvider = ProviderFactory(httpContext);
                    var context = new TrailContext(serviceProvider, httpContext, a);
                    await a.Invoke(context, match);
                    return;
                }
            }

            // didn't find a matching route - pass context to next middleware
            await _next(httpContext);
        }

        public void AddController(Type controller)
        {
            var controllerAttr = controller.GetCustomAttribute<TrailControllerAttribute>();
            if (controllerAttr.RoutePrefix.StartsWith(RegexRoutePrefix))
                throw new TrailMvcSetupException("A controller route prefix cannot be a regular expression. Controller: " + controller.FullName);

            var controllerPreRoutes = controller.GetCustomAttributes<TrailPreRouteAttribute>().ToArray();

            foreach (var method in controller.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            {
                var routeAttr = method.GetCustomAttribute<TrailRouteAttribute>();
                if (routeAttr == null)
                    continue;

                // don't use the route prefix if using a regex
                var route = routeAttr.Route.StartsWith(RegexRoutePrefix) ? routeAttr.Route : controllerAttr.RoutePrefix + routeAttr.Route;

                // figure out http methods
                var httpMethodsAttr = method.GetCustomAttribute<TrailHttpMethodsAttribute>();
                var httpMethods = httpMethodsAttr?.Methods ?? ExtractMethodFromName(method.Name);

                if (httpMethods.Contains("ANY"))
                    httpMethods = new string[0];

                // determine route priority
                var priorityAttr = method.GetCustomAttribute<TrailRoutePriorityAttribute>();
                var priority = priorityAttr?.Priority ?? DefaultRoutePriority;

                // pre-routes
                var preRoutes = method.GetCustomAttributes<TrailPreRouteAttribute>();
                var ignorePreRoutes = method.GetCustomAttribute<TrailIgnoreControllerPreRoutesAttribute>();
                if (ignorePreRoutes == null)
                    preRoutes = preRoutes.Concat(controllerPreRoutes);

                AddAction(new TrailAction(method, route, httpMethods, priority, preRoutes));
            }
        }

        public void AddAction(TrailAction action)
        {
            lock (_pendingActionsLock)
            {
                if (_pendingActions == null)
                    _pendingActions = new List<TrailAction>();

                _pendingActions.Add(action);
            }
        }

        public IEnumerable<Type> FindControllers()
        {
            foreach (var type in typeof(TrailMvc).Assembly.GetTypes())
            {
                if (type.IsClass && type.GetCustomAttribute<TrailControllerAttribute>() != null)
                    yield return type;
            }
        }

        private void AutoApplyControllers()
        {
            foreach (var controller in FindControllers())
            {
                AddController(controller);
            }
        }

        private void SetupPendingActions()
        {
            lock (_pendingActionsLock)
            {
                if (_pendingActions == null)
                    return;

                var actions = _actions.Concat(_pendingActions).ToArray();
                Array.Sort(actions, (a, b) => a.Priority - b.Priority);
                _pendingActions = null;
                _actions = actions;
            }
        }

        private string[] ExtractMethodFromName(string name)
        {
            var parts = name.Split('_');
            var methods = new string[parts.Length - 1];
            for (var i = 0; i < methods.Length; i++)
            {
                var m = parts[i + 1].ToUpperInvariant();
                if (m != "ANY" && !_validHttpMethods.Contains(m))
                    throw new TrailMvcSetupException(m + " does not represent a valid HTTP Method in Action " + name);

                methods[i] = m;
            }

            return methods;
        }
    }

    public class TrailParamTypeConverter
    {
        public string RegexTemplate { get; }
        public Func<string, object> Convert { get; }

        public TrailParamTypeConverter(string regexTemplate, Func<string, object> converter)
        {
            RegexTemplate = regexTemplate;
            Convert = converter;
        }
    }

    public class TrailAction
    {
        public MethodInfo MethodInfo { get; }
        public string Pattern { get; }
        public string[] HttpMethods { get; }
        public int Priority { get; }
        public TrailPreRouteAttribute[] PreRoutes { get; }

        private readonly ParameterInfo[] _parameters;
        private readonly Regex _patternRegex;

        public TrailAction(MethodInfo methodInfo, string pattern, string[] httpMethods, int priority, IEnumerable<TrailPreRouteAttribute> preRoutes)
        {
            // get params
            _parameters = methodInfo.GetParameters();
            if (_parameters.Length == 0 || _parameters[0].ParameterType != typeof(TrailContext))
            {
                throw new TrailMvcSetupException(String.Format("{0}.{1} is not a valid Trail action. It does not accept a TrailContext as its first parameter.",
                    methodInfo.DeclaringType.FullName, methodInfo.Name));
            }

            for (var i = 1; i < _parameters.Length; i++)
            {
                if (!TrailMvc.ParamTypeConverters.ContainsKey(_parameters[i].ParameterType))
                {
                    throw new TrailMvcSetupException(
                        String.Format("Parameter {0} of Type {1} in action {2}.{3} is not a vaild action parameter type. There is no mapping for it in TrailMvc.ParamTypeConverters.",
                           _parameters[i].Name, _parameters[i].ParameterType, methodInfo.DeclaringType.FullName, methodInfo.Name));
                }
            }

            MethodInfo = methodInfo;
            Pattern = pattern;
            HttpMethods = httpMethods;
            Priority = priority;
            PreRoutes = GetOrderedPreRoutes(preRoutes);

            _patternRegex = PatternToRegex(pattern, _parameters);
        }

        private TrailPreRouteAttribute[] GetOrderedPreRoutes(IEnumerable<TrailPreRouteAttribute> preRoutes)
        {
            var arr = preRoutes.ToArray();
            Array.Sort(arr, (a, b) => a.Order - b.Order);
            return arr;
        }

        public static Regex PatternToRegex(string pattern, ParameterInfo[] parameters)
        {
            if (pattern.StartsWith(TrailMvc.RegexRoutePrefix))
            {
                var sub = pattern.Substring(TrailMvc.RegexRoutePrefix.Length);
                var flagsEnd = sub.IndexOf(':');
                var flagsStr = sub.Substring(0, flagsEnd);
                var exp = sub.Substring(flagsEnd + 1);

                var options = RegexOptions.Compiled;
                if (flagsStr != "")
                {
                    foreach (var f in flagsStr.Split('|'))
                    {
                        options |= (RegexOptions)Enum.Parse(typeof(RegexOptions), f);
                    }
                }
                else
                {
                    options |= RegexOptions.IgnoreCase;
                }

                return new Regex(exp, options);
            }

            // todo check for duplicates and that all parameters are accounted for
            var paramRegex = new Regex(":[a-z_][a-z0-9_]*(?=/|$)", RegexOptions.IgnoreCase);
            var reg = paramRegex.Replace(Regex.Escape(pattern), match =>
            {
                var name = match.Value.Substring(1);
                var param = parameters.FirstOrDefault(p => p.Name == name);
                if (param == null)
                {
                    throw new TrailMvcSetupException(String.Format(""));
                }

                var converter = TrailMvc.ParamTypeConverters[param.ParameterType];

                return "(?<" + name + ">" + converter.RegexTemplate + ")";
            });
            if (reg.EndsWith("/")) // always consider a trailing slash optional
                reg += "?";
            return new Regex("^" + reg + "$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        }

        public Match Match(string method, string path)
        {
            if (!HttpMethods.Contains(method))
                return null;

            var match = _patternRegex.Match(path);
            return match.Success ? match : null;
        }

        public async Task Invoke(TrailContext context, Match match)
        {
            var next = CreateActionContinuation(context, match);

            for (var i = 0; i < PreRoutes.Length; i++)
            {
                next = CreatePreRouteContinuation(context, PreRoutes[i], next);
            }

            await next();
        }

        private TrailContinuationDelegate CreatePreRouteContinuation(TrailContext context, TrailPreRouteAttribute preRoute, TrailContinuationDelegate continuation)
        {
            var called = false;

            return async () =>
            {
                if (called)
                    throw new TrailMvcContinuationException("TrailMvc Continuation was called more than once."); // todo: should probably include more data

                called = true;
                context.Continue = continuation;
                await preRoute.Invoke(context);
            };
        }

        private TrailContinuationDelegate CreateActionContinuation(TrailContext context, Match match)
        {
            var called = false;

            // map arguments - do this before the closure so that we get errors before the pre-routes
            var p = new object[_parameters.Length];
            p[0] = context;

            for (var i = 1; i < p.Length; i++)
            {
                // todo figure out a good way to handle errors here
                var converter = TrailMvc.ParamTypeConverters[_parameters[i].ParameterType];
                p[i] = converter.Convert(match.Groups[_parameters[i].Name]?.Value);
            }

            return async () =>
            {
                if (called)
                    throw new TrailMvcContinuationException("TrailMvc Continuation was called more than once."); // todo: should probably include more data

                called = true;
                context.Continue = CreateNoOpContinuation();
                await (Task)MethodInfo.Invoke(null, p);
            };
        }

        private TrailContinuationDelegate CreateNoOpContinuation()
        {
            var called = false;

#pragma warning disable 1998 // ignore async method without await warning
            return async () =>
            {
                if (called)
                    throw new TrailMvcContinuationException("TrailMvc Continuation was called more than once."); // todo: should probably include more data

                called = true;
            };
#pragma warning restore 1998
        }
    }

    public class TrailMvcSetupException : Exception
    {
        public TrailMvcSetupException(string message, Exception innerException = null) : base(message, innerException)
        {
        }
    }

    public class TrailMvcContinuationException : Exception
    {
        public TrailMvcContinuationException(string message, Exception innerException = null) : base(message, innerException)
        {
        }
    }

    // ======================================================================================
    // Attributes
    // ======================================================================================

    public abstract class TrailAttribute : Attribute
    {
    }

    public class TrailRouteAttribute : TrailAttribute
    {
        public string Route { get; }

        public TrailRouteAttribute(string route)
        {
            Route = route;
        }
    }

    public class TrailControllerAttribute : TrailAttribute
    {
        public string RoutePrefix { get; }

        public TrailControllerAttribute(string routePrefix = null)
        {
            RoutePrefix = routePrefix;
        }
    }

    public class TrailIgnoreControllerPreRoutesAttribute : TrailAttribute
    {
    }

    public class TrailHttpMethodsAttribute : TrailAttribute
    {
        public string[] Methods { get; }

        public TrailHttpMethodsAttribute(params string[] methods)
        {
            for (var i = 0; i < methods.Length; i++)
            {
                methods[i] = methods[i].ToUpperInvariant();
            }

            Methods = methods;
        }
    }

    public class TrailRoutePriorityAttribute : TrailAttribute
    {
        public int Priority { get; }

        public TrailRoutePriorityAttribute(int priority)
        {
            Priority = priority;
        }
    }

    public abstract class TrailPreRouteAttribute : TrailAttribute
    {
        public int Order { get; protected set; } = int.MaxValue;
        public abstract Task Invoke(TrailContext context);
    }
}