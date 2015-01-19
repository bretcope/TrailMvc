using System;
using Microsoft.AspNet.Builder;
using Microsoft.AspNet.Http;

namespace TrailMvc
{
    public class Startup
    {
        public void Configure(IApplicationBuilder app)
        {
            app.UseTrailMvc(context => new CustomServiceProviderImpl());
        }
    }
    public interface ICustomServiceProvider
    {
        string SaySomething { get; set; }
    }

    public class CustomServiceProviderImpl : ICustomServiceProvider
    {
        public string SaySomething { get; set; } = "";
    }
}
