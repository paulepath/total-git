/**
 * Total Git website infrastructure: https://totalgit.e-path.co.uk
 *
 * - Azure Static Web App (Free) hosting `site/`, content deployed by .github/workflows/site.yml
 * - Cloudflare DNS for the custom domain (in the e-path.co.uk zone, shared with the other e-path sites)
 */
import * as pulumi from "@pulumi/pulumi";
import * as azure_native from "@pulumi/azure-native";
import * as cloudflare from "@pulumi/cloudflare";

const config = new pulumi.Config();
const zoneId = config.require("cloudflareZoneId");
const hostname = config.get("hostname") ?? "totalgit.e-path.co.uk";

// ---- Azure: hosting ----

const resourceGroup = new azure_native.resources.ResourceGroup("totalgit", {
    location: "westeurope",
    resourceGroupName: "totalgit",
});

// Static Web Apps only exist in a few regions; West Europe matches the other e-path sites.
const site = new azure_native.web.StaticSite("totalgit", {
    allowConfigFileUpdates: true,
    enterpriseGradeCdnStatus: azure_native.web.EnterpriseGradeCdnStatus.Disabled,
    location: "West Europe",
    name: "totalgit",
    // Content comes from the GitHub workflow with a deployment token, not a linked repository.
    provider: "SwaCli",
    resourceGroupName: resourceGroup.name,
    sku: { name: "Free", tier: "Free" },
    stagingEnvironmentPolicy: azure_native.web.StagingEnvironmentPolicy.Disabled,
}, { protect: true });

// ---- Cloudflare: DNS ----

// DNS-only (not proxied) so Azure can issue and renew the managed certificate.
const siteCname = new cloudflare.DnsRecord("totalgit-cname", {
    zoneId,
    name: hostname,
    type: "CNAME",
    content: site.defaultHostname,
    ttl: 300,
    proxied: false,
    comment: "Total Git website (Azure Static Web App totalgit)",
});

// ---- Azure: custom domain (needs the CNAME in place first) ----

new azure_native.web.StaticSiteCustomDomain("totalgit-domain", {
    domainName: hostname,
    name: site.name,
    resourceGroupName: resourceGroup.name,
}, { dependsOn: [siteCname] });

// The token the website workflow deploys with (stored as the AZURE_STATIC_WEB_APPS_API_TOKEN secret).
const secrets = azure_native.web.listStaticSiteSecretsOutput({ name: site.name, resourceGroupName: resourceGroup.name });

export const defaultHostname = site.defaultHostname;
export const url = `https://${hostname}`;
export const deploymentToken = pulumi.secret(secrets.properties.apply(p => p?.["apiKey"] ?? ""));
