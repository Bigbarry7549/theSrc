using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Playwright;
using Microsoft.Playwright.NUnit;
using NUnit.Framework;

namespace Ui.E2E.Tests;

public class LiferayTraceTests : PageTest
{
    [Test]
    public async Task Login_GuestHome()
    {
        var baseUrl = RequireEnv("LIFERAY_BASE_URL").TrimEnd('/');
        var user = RequireEnv("LIFERAY_ADMIN_USER");
        var pass = RequireEnv("LIFERAY_ADMIN_PASS");

        // Put artifacts in the project folder (tests/Ui.E2E/artifacts)
        var projectRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../"));
        var artifactsDir = Path.Combine(projectRoot, "artifacts");
        Directory.CreateDirectory(artifactsDir);

        var tracePath = Path.Combine(artifactsDir, "trace.zip");
        var homePng = Path.Combine(artifactsDir, "home.png");
        var debugPng = Path.Combine(artifactsDir, "debug.png");
        var loginHtml = Path.Combine(artifactsDir, "login.html");

        Page.SetDefaultTimeout(60_000);

        await Context.Tracing.StartAsync(new()
        {
            Screenshots = true,
            Snapshots = true,
            Sources = true
        });

        try
        {
            // 1) Go to Liferay login
            await Page.GotoAsync($"{baseUrl}/c/portal/login", new() { WaitUntil = WaitUntilState.DOMContentLoaded });

            // 2) If GitHub shows the "Codespaces Access Port" warning page, click Continue automatically
            await BypassCodespacesPortWarningAsync(Page, debugPng);

            Console.WriteLine($"Title after bypass: {await Page.TitleAsync()}");
            Console.WriteLine($"URL after bypass: {Page.Url}");

            // 3) Guard: if we still hit GitHub interstitial, fail with good artifacts
            var title = await Page.TitleAsync();
            if (title.Contains("Codespaces Access Port", StringComparison.OrdinalIgnoreCase))
            {
                await File.WriteAllTextAsync(loginHtml, await Page.ContentAsync());
                await Page.ScreenshotAsync(new() { Path = debugPng, FullPage = true });
                throw new Exception("Still on GitHub 'Codespaces Access Port' warning page. The Continue click/cookie didn’t take effect in this Playwright context.");
            }

            // 4) Guard: pf-signin (port not public / not accessible)
            var uri = new Uri(Page.Url);
            if (uri.AbsolutePath.StartsWith("/pf-signin", StringComparison.OrdinalIgnoreCase))
            {
                await File.WriteAllTextAsync(loginHtml, await Page.ContentAsync());
                await Page.ScreenshotAsync(new() { Path = debugPng, FullPage = true });
                throw new Exception("Hit Codespaces pf-signin (port 8080 is not Public). Set port 8080 visibility to Public, then re-run.");
            }

            // Save the rendered HTML + screenshot (so you can inspect selectors if it fails)
            await File.WriteAllTextAsync(loginHtml, await Page.ContentAsync());
            await Page.ScreenshotAsync(new() { Path = debugPng, FullPage = true });

            // 5) Flexible selectors (works across Liferay variations)
            var login = await WaitForAnyAsync(
                "input[name='_com_liferay_login_web_portlet_LoginPortlet_login']",
                "input[name*='LoginPortlet_login']",
                "input[name='login']",
                "input[id*='LoginPortlet_login']",
                "input[type='text']"
            );

            var password = await WaitForAnyAsync(
                "input[name='_com_liferay_login_web_portlet_LoginPortlet_password']",
                "input[name*='LoginPortlet_password']",
                "input[name='password']",
                "input[type='password']"
            );

            await login.FillAsync(user);
            await password.FillAsync(pass);

            // 6) Submit
            await Page.Locator("form").Locator("button[type=submit], input[type=submit]").First.ClickAsync();

            await Page.WaitForURLAsync("**/web/**", new() { Timeout = 60_000 });
            await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

            await Page.ScreenshotAsync(new() { Path = homePng, FullPage = true });
        }
        finally
        {
            await Context.Tracing.StopAsync(new() { Path = tracePath });
            Console.WriteLine($"Artifacts written to: {artifactsDir}");
        }
    }

    // Clicks "Continue" on the GitHub Codespaces port warning page (if it appears)
    private static async Task BypassCodespacesPortWarningAsync(IPage page, string debugPng)
    {
        var title = await page.TitleAsync();

        if (!title.Contains("Codespaces Access Port", StringComparison.OrdinalIgnoreCase))
            return; // not the warning page

        // Capture what we saw (useful if click fails)
        await page.ScreenshotAsync(new() { Path = debugPng, FullPage = true });

        // Click Continue (it sets the tunnel_phishing_protection cookie + reloads)
        var continueBtn = page.Locator("button:has-text('Continue')");
        await continueBtn.ClickAsync(new() { Timeout = 10_000 });

        // Wait until we're not on the warning page anymore
        await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);

        await page.WaitForFunctionAsync(
    "() => document.title && !document.title.toLowerCase().includes('codespaces access port')",
    null,
    new PageWaitForFunctionOptions { Timeout = 30_000 }
);

    }

    private async Task<ILocator> WaitForAnyAsync(params string[] selectors)
    {
        var start = DateTime.UtcNow;

        while ((DateTime.UtcNow - start).TotalSeconds < 30)
        {
            foreach (var s in selectors)
            {
                var loc = Page.Locator(s);
                if (await loc.CountAsync() > 0)
                {
                    return loc.First;
                }
            }

            await Task.Delay(250);
        }

        throw new TimeoutException("None of the expected selectors appeared. Check artifacts/debug.png and artifacts/login.html.");
    }

    private static string RequireEnv(string key) =>
        Environment.GetEnvironmentVariable(key) ?? throw new Exception($"Missing env var: {key}");
}
