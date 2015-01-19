using System.Threading.Tasks;
using Microsoft.AspNet.Http;

namespace TrailMvc
{
    [TrailController("/test")]
    public static class TestController
    {
        [TrailRoute("/")]
        public static async Task Index_Get(TrailContext context)
        {
            await context.HttpContext.Response.WriteAsync("index get");
        }

        [TrailRoute("/")]
        public static async Task Index_Post(TrailContext context)
        {
            await context.Response.WriteAsync("index post");
        }

        [PreSomething]
        [PreSomething1]
        [TrailRoute("/interesting/:id")]
        public static async Task Interesting_Get(TrailContext context, int id)
        {
            await context.Response.WriteAsync("interesting get. ID: " + id + " Said: " + context.ServiceProviderProperty.SaySomething);
        }
    }

    public class PreSomethingAttribute : TrailPreRouteAttribute
    {
        public PreSomethingAttribute()
        {
            Order = 1;
        }

        public override async Task Invoke(TrailContext context)
        {
            context.ServiceProviderProperty.SaySomething += " pre-something";
            await context.Continue();
        }
    }

    public class PreSomething1Attribute : TrailPreRouteAttribute
    {
        public PreSomething1Attribute()
        {
            Order = 2;
        }

        public override async Task Invoke(TrailContext context)
        {
            context.ServiceProviderProperty.SaySomething += " one";
            await context.Continue();
        }
    }
}