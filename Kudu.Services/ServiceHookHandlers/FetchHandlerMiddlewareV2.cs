﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Kudu.Contracts.Settings;
using Kudu.Contracts.Tracing;
using Kudu.Core.Deployment;
using Kudu.Core.Tracing;
using Kudu.Services.Infrastructure;
using Kudu.Services.ServiceHookHandlers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Kudu.Services
{
    /// <summary>
    /// Terminal middleware to handle fetch requests for custom Git/BitBucket
    /// deployment. Webhooks send
    /// </summary>
    /// <exception cref="System.OverflowException">Thrown when one parameter is max 
    /// and the other is greater than 0.</exception>
    public class FetchHandlerMiddlewareV2
    {
        public FetchHandlerMiddlewareV2(
            RequestDelegate next)
        {
            // next is never used, this middleware is always terminal
        }

        public async Task Invoke(
            HttpContext context,
            ITracer tracer,
            IDeploymentSettingsManager settings,
            IFetchDeploymentManager manager,
            IEnumerable<IServiceHookHandler> serviceHookHandlers
            )
        {
            using (tracer.Step("FetchHandler"))
            {
                // Redirect GET /deploy requests to the Kudu root for convenience when using URL from Azure portal
                if (string.Equals(context.Request.Method, "GET", StringComparison.OrdinalIgnoreCase))
                {
                    context.Response.Redirect("/");
                    return;
                }

                if (!string.Equals(context.Request.Method, "POST", StringComparison.OrdinalIgnoreCase))
                {
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }

                // CORE TODO Need to set up UseDeveloperExceptionPage, UseExceptionHandler or the like in startup
                //context.Response.TrySkipIisCustomErrors = true;

                DeploymentInfoBase deployInfo = null;

                // We are going to assume that the branch details are already set by the time it gets here. This is
                // particularly important in the mercurial case,since Settings hardcodes the default value for Branch
                // to be "master". Consequently, Kudu will NoOp requests for Mercurial commits.
                var targetBranch = settings.GetBranch();
                try
                {
                    var payload = GetPayload(context.Request, tracer);
                    DeployAction action = GetRepositoryInfo(context.Request, payload, targetBranch, serviceHookHandlers, tracer, out deployInfo);
                    if (action == DeployAction.NoOp)
                    {
                        tracer.Trace("No-op for deployment.");
                        return;
                    }
                }
                catch (FormatException ex)
                {
                    tracer.TraceError(ex);
                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                    await context.Response.WriteAsync(ex.Message);
                    return;
                }

                // CORE TODO make sure .Query has the same semantics as the old .QueryString (null, empty, etc.)
                var asyncRequested = string.Equals(context.Request.Query["isAsync"], "true", StringComparison.OrdinalIgnoreCase);

                var response = await manager.FetchDeploy(deployInfo, asyncRequested, UriHelper.GetRequestUri(context.Request), targetBranch);

                switch (response)
                {
                    case FetchDeploymentRequestResult.RunningAynschronously:
                        // to avoid regression, only set location header if isAsync
                        if (asyncRequested)
                        {
                            // latest deployment keyword reserved to poll till deployment done
                            context.Response.Headers["Location"] = new Uri(UriHelper.GetRequestUri(context.Request),
                                String.Format("/api/deployments/{0}?deployer={1}&time={2}", Constants.LatestDeployment, deployInfo.Deployer, DateTime.UtcNow.ToString("yyy-MM-dd_HH-mm-ssZ"))).ToString();
                        }
                        context.Response.StatusCode = StatusCodes.Status202Accepted;
                        return;
                    case FetchDeploymentRequestResult.ForbiddenScmDisabled:
                        context.Response.StatusCode = StatusCodes.Status403Forbidden;
                        tracer.Trace("Scm is not enabled, reject all requests.");
                        return;
                    case FetchDeploymentRequestResult.ConflictAutoSwapOngoing:
                        context.Response.StatusCode = StatusCodes.Status409Conflict;
                        await context.Response.WriteAsync(Resources.Error_AutoSwapDeploymentOngoing);
                        return;
                    case FetchDeploymentRequestResult.Pending:
                        // Return a http 202: the request has been accepted for processing, but the processing has not been completed.
                        context.Response.StatusCode = StatusCodes.Status202Accepted;
                        return;
                    case FetchDeploymentRequestResult.ConflictDeploymentInProgress:
                        context.Response.StatusCode = StatusCodes.Status409Conflict;
                        await context.Response.WriteAsync(Resources.Error_DeploymentInProgress);
                        return;
                    case FetchDeploymentRequestResult.ConflictRunFromRemoteZipConfigured:
                        context.Response.StatusCode = StatusCodes.Status409Conflict;
                        await context.Response.WriteAsync(Resources.Error_RunFromRemoteZipConfigured);
                        return;
                    case FetchDeploymentRequestResult.RanSynchronously:
                    default:
                        context.Response.StatusCode = StatusCodes.Status200OK;
                        break;
                }
            }
        }

        private DeployAction GetRepositoryInfo(
            HttpRequest request, 
            JObject payload, 
            string targetBranch,
            IEnumerable<IServiceHookHandler> serviceHookHandlers,
            ITracer tracer,
            out DeploymentInfoBase info)
        {
            foreach (var handler in serviceHookHandlers)
            {
                DeployAction result = handler.TryParseDeploymentInfo(request, payload, targetBranch, out info);
                if (result == DeployAction.UnknownPayload) continue;
                if (tracer.TraceLevel >= TraceLevel.Verbose)
                {
                    var attribs = new Dictionary<string, string>
                    {
                        { "type", handler.GetType().FullName }
                    };
                    tracer.Trace("handler", attribs);
                }

                if (result == DeployAction.ProcessDeployment)
                {
                    // Although a payload may be intended for a handler, it might not need to fetch.
                    // For instance, if a different branch was pushed than the one the repository is deploying,
                    // we can no-op it.
                    Debug.Assert(info != null);
                    info.Fetch = handler.Fetch;
                }

                return result;
            }

            throw new FormatException(Resources.Error_UnsupportedFormat);
        }
        
        
        private JObject GetPayload(HttpRequest contextRequest, ITracer tracer)
        {
            JObject payload;

            if (contextRequest.HasFormContentType && contextRequest.Form.Count > 0)
            {
                string json = contextRequest.Form["payload"];
                if (string.IsNullOrEmpty(json))
                {
                    json = contextRequest.Form.First().Value;
                }
                payload = JsonConvert.DeserializeObject<JObject>(json);
            }
            else
            {
                using (var reader = new JsonTextReader(new StreamReader(contextRequest.Body)))
                {
                    payload = JObject.Load(reader);
                }
            }

            if (payload == null)
            {
                throw new FormatException(Resources.Error_EmptyPayload);
            }

            if (tracer.TraceLevel >= System.Diagnostics.TraceLevel.Verbose)
            {
                var attribs = new Dictionary<string, string>
                {
                    { "json", payload.ToString() }
                };

                tracer.Trace("payload", attribs);
            }
            return payload;
        }
    }
    
    public static class FetchHandlerV2Extensions
    {
        public static IApplicationBuilder RunFetchHandler(this IApplicationBuilder builder)
        {
            return builder.UseMiddleware<FetchHandlerMiddleware>();
        }
    }
}