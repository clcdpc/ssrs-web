using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.ViewEngines;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using SsrsPortal.Core;

namespace SsrsPortal.Tests;

// Render structural edge cases through the production partial, including layouts
// that the current folder-only AllowedRoots policy cannot produce from SOAP.
public static class BreadcrumbRendering
{
    public static async Task<string> RenderAsync(IServiceProvider services, BreadcrumbNavigation navigation)
    {
        using var scope = services.CreateScope();
        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        context.Request.Scheme = "https"; context.Request.Host = new HostString("localhost");
        context.SetEndpoint(scope.ServiceProvider.GetRequiredService<EndpointDataSource>().Endpoints
            .First(e => e.Metadata.GetMetadata<PageActionDescriptor>()?.ViewEnginePath == "/Index"));
        var routes = new RouteData(); routes.Values["page"] = "/Index";
        var action = new ActionContext(context, routes, new ActionDescriptor());
        var view = scope.ServiceProvider.GetRequiredService<ICompositeViewEngine>().GetView(null, "/Pages/Shared/_Breadcrumbs.cshtml", false);
        if (!view.Success) { throw new InvalidOperationException("Shared breadcrumb partial was not found."); }
        var data = new ViewDataDictionary<BreadcrumbNavigation>(new EmptyModelMetadataProvider(), new ModelStateDictionary()) { Model = navigation };
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        var temp = new TempDataDictionary(context, scope.ServiceProvider.GetRequiredService<ITempDataProvider>());
        await view.View.RenderAsync(new ViewContext(action, view.View, data, temp, writer, new HtmlHelperOptions()));
        return writer.ToString();
    }
}
