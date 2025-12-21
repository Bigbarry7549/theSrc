using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Playwright;
using Microsoft.Playwright.NUnit;
using NUnit.Framework;

namespace Ui.E2E.Tests;

[TestFixture]
public class LiferayTraceTests : PageTest
{
    [Test]
    public async Task Login_GuestHome()
    {
        var baseUrl = RequireEnv("LIFERAY_BASE_URL").TrimEnd('/');

        var user = RequireEnv("LIFERAY_ADMIN_USER");

        // Backward-compatible:
        // - If you set LIFERAY_ADMIN_PASS, we'll use it as the login password.
        // - If you set LIFERAY_ADMIN_PASS_CURRENT, we prefer that as the login password.
        var passCurrent =
            Environment.GetEnvironmentVariable("LIFERAY_ADMIN_PASS_CURRENT") ??
            Environment.GetEnvironmentVariable("LIFERAY_ADMIN_PASS") ??
            throw new Exception("Missing env var: LIFERAY_ADMIN_PASS_CURRENT (or LIFERAY_ADMIN_PASS)");

        // New password for the forced-reset flow
        var passNew = Environment.GetEnvironmentVariable("LIFERAY_ADMIN_PASS_NEW");

        var projectRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../"));
        var artifactsDir = Path.Combine(projectRoot, "artifacts");
        Directory.CreateDirectory(artifactsDir);

        var tracePath = Path.Combine(artifactsDir, "trace.zip");
        var beforeLoginPng = Path.Combine(artifactsDir, "before_login.png");
        var afterLoginPng = Path.Combine(artifactsDir, "after_login.png");
        var failPng = Path.Combine(artifactsDir, "fail.png");
        var failHtml = Path.Combine(artifactsDir, "fail.html");

        Page.SetDefaultTimeout(90_000);

        await Context.Tracing.StartAsync(new()
        {
            Screenshots = true,
            Snapshots = true,
            Sources = true
        });

        try
        {
            await Page.GotoAsync($"{baseUrl}/c/portal/login", new() { WaitUntil = WaitUntilState.DOMContentLoaded });

            await BypassCodespacesAccessPortWarningIfPresentAsync();
            await EnsureCookieSupportAsync(baseUrl);

            await Page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
            await Page.WaitForTimeoutAsync(800);

            var form = await FindVisibleLoginFormAsync(baseUrl);

            Console.WriteLine($"Login form found in frame: {(form.Frame.Url ?? "(no url)")}");

            await Page.ScreenshotAsync(new() { Path = beforeLoginPng, FullPage = true });

            // Fill only inside the same form that contains the visible password field
            await form.Login.FillAsync(user);
            await form.Password.FillAsync(passCurrent);

            // Assert we really filled the correct inputs before we submit
            var loginVal = await form.Login.InputValueAsync();
            var passVal = await form.Password.InputValueAsync();

            Console.WriteLine($"Filled login length={loginVal?.Length ?? 0}, pass length={passVal?.Length ?? 0}");

            if (string.IsNullOrWhiteSpace(loginVal) || string.IsNullOrWhiteSpace(passVal))
            {
                throw new Exception(
                    "Refusing to submit: login/password inputs are still empty. " +
                    "This means we selected the wrong inputs (or they are not fillable)."
                );
            }

            await form.Submit.ClickAsync();

            await Page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
            await Page.WaitForTimeoutAsync(1200);

            // If login failed, Liferay shows obvious error text on the same page
            var bodyText = (await Page.TextContentAsync("body")) ?? "";
            if (bodyText.Contains("Authentication failed", StringComparison.OrdinalIgnoreCase) ||
                bodyText.Contains("incorrect credentials", StringComparison.OrdinalIgnoreCase) ||
                bodyText.Contains("Password field is required", StringComparison.OrdinalIgnoreCase) ||
                bodyText.Contains("Email Address field is required", StringComparison.OrdinalIgnoreCase))
            {
                throw new Exception("Login failed (Liferay error text detected). Check fail.html for exact message.");
            }

            // STEP 1: Handle forced "New Password" page (often only 2 fields: Password + Reenter Password)
            await MaybeCompleteForcedPasswordResetAsync(baseUrl, passNew);

            // Logged-in signals (broad)
            await WaitForAnyVisibleAsync(
                "a[href*='/c/portal/logout']",
                "a[href*='c/portal/logout']",
                ".control-menu",
                "#controlMenu",
                ".product-menu",
                ".lfr-product-menu-sidebar",
                ".user-portrait",
                "img.user-icon",
                "a[aria-label*='User' i]"
            );

            await Page.ScreenshotAsync(new() { Path = afterLoginPng, FullPage = true });
            Console.WriteLine($"Final URL: {Page.Url}");
        }
        catch
        {
            Console.WriteLine("URL at failure: " + Page.Url);
            await Page.ScreenshotAsync(new() { Path = failPng, FullPage = true });
            await File.WriteAllTextAsync(failHtml, await Page.ContentAsync());
            throw;
        }
        finally
        {
            await Context.Tracing.StopAsync(new() { Path = tracePath });
            Console.WriteLine($"Artifacts written to: {artifactsDir}");
        }
    }

    // ============================================================
    // EXTRA TEST: verify Product Menu + Page Tree (from your video)
    // ============================================================
    [Test]
public async Task ProductMenu_VerifyVideoStructure()
{
    var (artifactsDir, tracePath) = InitArtifacts(nameof(ProductMenu_VerifyVideoStructure));
    var stepPng = Path.Combine(artifactsDir, "step.png");
    var failPng = Path.Combine(artifactsDir, "fail.png");
    var failHtml = Path.Combine(artifactsDir, "fail.html");

    Page.SetDefaultTimeout(90_000);

    await Context.Tracing.StartAsync(new()
    {
        Screenshots = true,
        Snapshots = true,
        Sources = true
    });

    try
    {
        await LoginOnlyAsync();

        // Screenshot after login (baseline)
        await Page.ScreenshotAsync(new() { Path = Path.Combine(artifactsDir, "01_after_login.png"), FullPage = true });

        // Open Product Menu
        var sidebar = await EnsureProductMenuOpenAsync();
        await EnsureProductMenuRootAsync(sidebar);

        await Page.ScreenshotAsync(new() { Path = Path.Combine(artifactsDir, "02_product_menu_open.png"), FullPage = true });

        var expected = new (string Section, string[] Children)[]
        {
            ("Home", Array.Empty<string>()),
            ("Page Tree", Array.Empty<string>()),

            ("Design", new[] { "Style Books", "Fragments", "Templates", "Page Templates" }),

            ("Site Builder", new[] { "Pages", "Navigation Menus", "Collections" }),

            ("Content & Data", new[]
            {
                "Web Content", "Blogs", "Bookmarks", "Documents and Media", "Forms",
                "Knowledge Base", "Message Boards", "Translation Processes"
            }),

            ("Categorization", new[] { "Categories", "Tags" }),

            ("Recycle Bin", new[] { "Recycle Bin" }),

            ("People", new[] { "Memberships", "Teams", "Segments" }),

            ("Configuration", new[] { "Site Settings", "Redirection", "Locked Pages", "Workflow" }),

            ("Publishing", new[] { "Staging", "Export", "Import" })
        };

        // 1) Assert top-level items exist
        foreach (var (section, _) in expected)
            await ExpectSidebarTextVisibleAsync(sidebar, section);

        await Page.ScreenshotAsync(new() { Path = Path.Combine(artifactsDir, "03_top_level_verified.png"), FullPage = true });

        // 2) Expand and verify children
        foreach (var (section, children) in expected)
        {
            if (children.Length == 0) continue;

            await ClickSidebarEntryAsync(sidebar, section);
            await ExpectSidebarTextVisibleAsync(sidebar, children[0]);

            foreach (var child in children)
                await ExpectSidebarTextVisibleAsync(sidebar, child);
        }

        await Page.ScreenshotAsync(new() { Path = Path.Combine(artifactsDir, "04_submenus_verified.png"), FullPage = true });

        // 3) Page Tree panel checks
        await ClickSidebarEntryAsync(sidebar, "Page Tree");

        await ExpectAnyVisibleAsync(
            "text=Back to Menu",
            "text=Start typing to find a page",
            "text=Pages Hierarchy",
            "text=Go to Pages Administration"
        );

        await Page.ScreenshotAsync(new() { Path = Path.Combine(artifactsDir, "05_page_tree_panel.png"), FullPage = true });

        // Optional: pages list
        await ExpectAnyVisibleAsync("text=Home", "text=Search");

        Console.WriteLine($"Artifacts written to: {artifactsDir}");
    }
    catch
    {
        Console.WriteLine("URL at failure: " + Page.Url);
        await Page.ScreenshotAsync(new() { Path = failPng, FullPage = true });
        await File.WriteAllTextAsync(failHtml, await Page.ContentAsync());
        throw;
    }
    finally
    {
        await Context.Tracing.StopAsync(new() { Path = tracePath });
        Console.WriteLine($"Trace written to: {tracePath}");
    }
}

    

    private (string artifactsDir, string tracePath) InitArtifacts(string testName)
{
    var projectRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../"));
    var artifactsDir = Path.Combine(projectRoot, "artifacts", testName);
    Directory.CreateDirectory(artifactsDir);

    var tracePath = Path.Combine(artifactsDir, "trace.zip");
    return (artifactsDir, tracePath);
}


    // -------------------------
    // UI helpers for menu tests
    // -------------------------
    private async Task LoginOnlyAsync()
    {
        var baseUrl = RequireEnv("LIFERAY_BASE_URL").TrimEnd('/');
        var user = RequireEnv("LIFERAY_ADMIN_USER");

        var passCurrent =
            Environment.GetEnvironmentVariable("LIFERAY_ADMIN_PASS_CURRENT") ??
            Environment.GetEnvironmentVariable("LIFERAY_ADMIN_PASS") ??
            throw new Exception("Missing env var: LIFERAY_ADMIN_PASS_CURRENT (or LIFERAY_ADMIN_PASS)");

        var passNew = Environment.GetEnvironmentVariable("LIFERAY_ADMIN_PASS_NEW");

        Page.SetDefaultTimeout(90_000);

        await Page.GotoAsync($"{baseUrl}/c/portal/login", new() { WaitUntil = WaitUntilState.DOMContentLoaded });

        await BypassCodespacesAccessPortWarningIfPresentAsync();
        await EnsureCookieSupportAsync(baseUrl);

        await Page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
        await Page.WaitForTimeoutAsync(800);

        var form = await FindVisibleLoginFormAsync(baseUrl);

        await form.Login.FillAsync(user);
        await form.Password.FillAsync(passCurrent);
        await form.Submit.ClickAsync();

        await Page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
        await Page.WaitForTimeoutAsync(1200);

        await MaybeCompleteForcedPasswordResetAsync(baseUrl, passNew);

        await WaitForAnyVisibleAsync(
            "a[href*='/c/portal/logout']",
            ".control-menu",
            "#controlMenu",
            ".product-menu",
            ".lfr-product-menu-sidebar",
            "img.user-icon",
            "a[aria-label*='User' i]"
        );
    }

    private async Task<ILocator> EnsureProductMenuOpenAsync()
    {
        // Common sidebar roots in Liferay
        var sidebarCandidates = new[]
        {
            ".lfr-product-menu-sidebar",
            ".product-menu"
        };

        // If already open, return it
        foreach (var sel in sidebarCandidates)
        {
            var loc = Page.Locator(sel).First;
            try
            {
                if (await loc.CountAsync() > 0 && await loc.IsVisibleAsync())
                    return loc;
            }
            catch { }
        }

        // Otherwise click the toggle in the top bar
        var toggle = await FirstVisibleOnPageAsync(
            "button[aria-label*='Product Menu' i]",
            "button[title*='Product Menu' i]",
            ".product-menu-toggle",
            "button:has(i.lexicon-icon-product-menu)",
            "button:has(i.lexicon-icon-bars)"
        );

        await toggle.ClickAsync();
        await Page.WaitForTimeoutAsync(500);

        // Wait for sidebar to show
        foreach (var sel in sidebarCandidates)
        {
            var loc = Page.Locator(sel).First;
            try
            {
                if (await loc.CountAsync() > 0)
                {
                    await loc.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 10_000 });
                    return loc;
                }
            }
            catch { }
        }

        throw new TimeoutException("Product Menu did not open (sidebar not visible).");
    }

    private async Task EnsureProductMenuRootAsync(ILocator sidebar)
{
    // If we're in Page Tree panel, it shows "Back to Menu"
    var back = sidebar.GetByText("Back to Menu", new() { Exact = true }).First;

    try
    {
        if (await back.CountAsync() > 0 && await back.IsVisibleAsync())
        {
            await back.ClickAsync();
            await Page.WaitForTimeoutAsync(500);
        }
    }
    catch { }

    // Wait until a known Product Menu item is visible
    await sidebar.GetByText("Design", new() { Exact = true }).First
        .WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 20_000 });
}


    private async Task ClickSidebarEntryAsync(ILocator sidebar, string text)
    {
        // Prefer role=link (cleaner), fallback to text
        ILocator entry = sidebar.GetByRole(AriaRole.Link, new() { Name = text, Exact = true }).First;

        try
        {
            if (await entry.CountAsync() == 0 || !await entry.IsVisibleAsync())
                entry = sidebar.GetByText(text, new() { Exact = true }).First;
        }
        catch
        {
            entry = sidebar.GetByText(text, new() { Exact = true }).First;
        }

        await entry.ClickAsync();
        await Page.WaitForTimeoutAsync(300);
    }

    private async Task ExpectSidebarTextVisibleAsync(ILocator sidebar, string text)
    {
        var loc = sidebar.GetByText(text, new() { Exact = true }).First;
        await loc.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 10_000 });
    }

    private async Task ExpectAnyVisibleAsync(params string[] selectors)
    {
        var start = DateTime.UtcNow;

        while ((DateTime.UtcNow - start).TotalSeconds < 15)
        {
            foreach (var s in selectors)
            {
                var loc = Page.Locator(s).First;
                try
                {
                    if (await loc.IsVisibleAsync())
                        return;
                }
                catch { }
            }

            await Task.Delay(250);
        }

        throw new TimeoutException("None of the expected UI signals became visible.");
    }

    private async Task<ILocator> FirstVisibleOnPageAsync(params string[] selectors)
    {
        foreach (var s in selectors)
        {
            var loc = Page.Locator(s).First;
            try
            {
                if (await loc.CountAsync() > 0 && await loc.IsVisibleAsync())
                    return loc;
            }
            catch { }
        }

        // Small retry window for slow-loading topbar icons
        await Page.WaitForTimeoutAsync(800);

        foreach (var s in selectors)
        {
            var loc = Page.Locator(s).First;
            try
            {
                if (await loc.CountAsync() > 0 && await loc.IsVisibleAsync())
                    return loc;
            }
            catch { }
        }

        throw new TimeoutException("Could not find a visible element for any provided selectors.");
    }

    // ============================================================
    // Your existing helpers continue below (unchanged)
    // ============================================================

    private sealed record LoginForm(IFrame Frame, ILocator Login, ILocator Password, ILocator Submit);

    private async Task EnsureCookieSupportAsync(string baseUrl)
    {
        var host = new Uri(baseUrl).Host;

        // Liferay uses this cookie as a cookie-support flag.
        // If its cookie support logic fails in automation, forcing this helps.
        await Context.AddCookiesAsync(new[]
        {
            new Cookie
            {
                Name = "COOKIE_SUPPORT",
                Value = "true",
                Domain = host,
                Path = "/"
            }
        });
    }

    private async Task BypassCodespacesAccessPortWarningIfPresentAsync()
    {
        for (var i = 0; i < 6; i++)
        {
            string title = "";
            try { title = await Page.TitleAsync(); } catch { }

            var hasWarningText =
                await Page.Locator("text=You are about to access a development port served by someone's codespace").CountAsync() > 0;

            var hasContinueButton =
                await Page.Locator("button:has-text('Continue')").CountAsync() > 0;

            if (title.Contains("Codespaces Access Port", StringComparison.OrdinalIgnoreCase) || (hasWarningText && hasContinueButton))
            {
                Console.WriteLine("Detected Codespaces 'Access Port' warning page. Clicking Continue...");
                await Page.Locator("button:has-text('Continue')").First.ClickAsync();
                await Page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
                await Page.WaitForTimeoutAsync(600);
                return;
            }

            await Page.WaitForTimeoutAsync(250);
        }
    }

    private async Task<LoginForm> FindVisibleLoginFormAsync(string baseUrl)
    {
        // Try anywhere (main + child frames)
        var found = await TryFindLoginAnywhereAsync();
        if (found != null) return found;

        // Try clicking "Sign In" link/button if it reveals the form
        foreach (var sel in new[]
        {
            "a:has-text('Sign In')",
            "button:has-text('Sign In')",
            "a:has-text('Sign in')",
            "button:has-text('Sign in')",
            "a:has-text('Log In')",
            "button:has-text('Log In')"
        })
        {
            var loc = Page.Locator(sel).First;
            try
            {
                if (await loc.CountAsync() > 0 && await loc.IsVisibleAsync())
                {
                    await loc.ClickAsync();
                    await Page.WaitForTimeoutAsync(800);

                    found = await TryFindLoginAnywhereAsync();
                    if (found != null) return found;
                }
            }
            catch { }
        }

        // Navigate directly to login portlet render URL
        var directLoginUrl = $"{baseUrl}/web/guest/home" +
                             "?p_p_id=com_liferay_login_web_portlet_LoginPortlet" +
                             "&p_p_lifecycle=0&p_p_state=maximized&p_p_mode=view" +
                             "&_com_liferay_login_web_portlet_LoginPortlet_mvcRenderCommandName=%2Flogin%2Flogin" +
                             "&saveLastPath=false";

        await Page.GotoAsync(directLoginUrl, new() { WaitUntil = WaitUntilState.DOMContentLoaded });
        await BypassCodespacesAccessPortWarningIfPresentAsync();
        await Page.WaitForTimeoutAsync(800);

        found = await TryFindLoginAnywhereAsync();
        if (found != null) return found;

        throw new TimeoutException("Could not find a visible login form (password field) in any frame.");
    }

    private async Task<LoginForm?> TryFindLoginAnywhereAsync()
    {
        foreach (var frame in Page.Frames)
        {
            var form = await TryFindLoginInFrameAsync(frame);
            if (form != null) return form;
        }
        return null;
    }

    private async Task<LoginForm?> TryFindLoginInFrameAsync(IFrame frame)
    {
        // Find a *visible* password input first
        var pw = await FirstVisibleInFrameAsync(frame,
            "input[id*='LoginPortlet_password']",
            "input[name*='LoginPortlet_password']",
            "input[autocomplete='current-password']",
            "input[type='password']"
        );

        if (pw == null) return null;

        // Restrict everything to the SAME form that contains that password input
        var form = frame.Locator("form").Filter(new() { Has = pw }).First;

        if (await form.CountAsync() == 0 || !await form.IsVisibleAsync())
        {
            // fallback: still try scoping to body, but prefer form
            form = frame.Locator("body");
        }

        // Now find login input inside that same form ONLY
        var login = await FirstVisibleInLocatorAsync(form,
            "input[id*='LoginPortlet_login']",
            "input[name*='LoginPortlet_login']",
            "input[autocomplete='username']",
            "input[type='email']",
            "input[type='text']"
        );

        // Submit inside same form
        var submit = await FirstVisibleInLocatorAsync(form,
            "button[type='submit']",
            "input[type='submit']",
            "button:has-text('Sign In')",
            "button:has-text('Sign in')",
            "button:has-text('Log In')",
            "button:has-text('Log in')"
        );

        return new LoginForm(frame, login, pw, submit);
    }

    private async Task<ILocator?> FirstVisibleInFrameAsync(IFrame frame, params string[] selectors)
    {
        foreach (var s in selectors)
        {
            var loc = frame.Locator(s).First;
            try
            {
                if (await loc.CountAsync() > 0 && await loc.IsVisibleAsync())
                    return loc;
            }
            catch { }
        }
        return null;
    }

    private async Task<ILocator> FirstVisibleInLocatorAsync(ILocator scope, params string[] selectors)
    {
        var start = DateTime.UtcNow;

        while ((DateTime.UtcNow - start).TotalSeconds < 10)
        {
            foreach (var s in selectors)
            {
                var loc = scope.Locator(s).First;
                try
                {
                    if (await loc.CountAsync() > 0 && await loc.IsVisibleAsync())
                        return loc;
                }
                catch { }
            }

            await Task.Delay(250);
        }

        throw new TimeoutException("Could not find a visible element for provided selectors in the scoped form.");
    }

    private async Task MaybeCompleteForcedPasswordResetAsync(string baseUrl, string? passNew)
    {
        // Give the post-login navigation a moment
        await Page.WaitForTimeoutAsync(600);

        string title = "";
        try { title = await Page.TitleAsync(); } catch { }

        // Strong signal: title becomes "New Password - Liferay"
        var looksLikeNewPasswordPage =
            title.Contains("New Password", StringComparison.OrdinalIgnoreCase) ||
            title.Contains("Change Password", StringComparison.OrdinalIgnoreCase);

        // Also detect by DOM: two password inputs visible on the page (often new + reenter)
        var visiblePwInputs = 0;
        try
        {
            // Any visible password inputs on the *main page* (not iframes)
            // If this ever lives in a frame in your setup, we can extend later.
            visiblePwInputs = await Page.Locator("input[type='password']:visible").CountAsync();
        }
        catch { }

        if (!looksLikeNewPasswordPage && visiblePwInputs < 2)
            return;

        Console.WriteLine($"Detected forced password reset page (title='{title}', visiblePwInputs={visiblePwInputs}).");

        if (string.IsNullOrWhiteSpace(passNew))
        {
            throw new Exception(
                "Liferay requires a password reset, but LIFERAY_ADMIN_PASS_NEW is not set. " +
                "Set LIFERAY_ADMIN_PASS_NEW and re-run."
            );
        }

        // Find the two password fields (new + confirm). On Liferay this is commonly exactly two visible password boxes.
        var pw1 = Page.Locator("input[type='password']:visible").Nth(0);
        var pw2 = Page.Locator("input[type='password']:visible").Nth(1);

        await pw1.FillAsync(passNew);
        await pw2.FillAsync(passNew);

        // Click a submit button on that page.
        // Common Liferay patterns: button[type=submit], or a button with "Save", "Change", or "Submit".
        var submitCandidates = new[]
        {
            "button[type='submit']",
            "input[type='submit']",
            "button:has-text('Save')",
            "button:has-text('Change')",
            "button:has-text('Submit')",
            "button:has-text('Update')"
        };

        ILocator? submit = null;
        foreach (var s in submitCandidates)
        {
            var loc = Page.Locator(s).First;
            try
            {
                if (await loc.CountAsync() > 0 && await loc.IsVisibleAsync())
                {
                    submit = loc;
                    break;
                }
            }
            catch { }
        }

        if (submit == null)
            throw new Exception("Detected password reset page but could not find a visible submit button.");

        await submit.ClickAsync();
        await Page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
        await Page.WaitForTimeoutAsync(1200);

        // If it *still* shows "New Password" after submit, likely the password policy rejected it.
        string titleAfter = "";
        try { titleAfter = await Page.TitleAsync(); } catch { }

        var bodyText = (await Page.TextContentAsync("body")) ?? "";
        if (titleAfter.Contains("New Password", StringComparison.OrdinalIgnoreCase) ||
            bodyText.Contains("Error", StringComparison.OrdinalIgnoreCase) ||
            bodyText.Contains("required", StringComparison.OrdinalIgnoreCase) ||
            bodyText.Contains("must", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("After password reset submit, page still looks like 'New Password' or has errors.");
            // Don’t over-guess the policy text; fail with a clear message so you inspect fail.html
            // (fail.html will be saved by outer catch).
        }

        Console.WriteLine("Password reset flow submitted. Continuing to wait for logged-in signals...");
    }

    private async Task WaitForAnyVisibleAsync(params string[] selectors)
    {
        var start = DateTime.UtcNow;

        while ((DateTime.UtcNow - start).TotalSeconds < 60)
        {
            foreach (var s in selectors)
            {
                var loc = Page.Locator(s).First;
                try
                {
                    if (await loc.IsVisibleAsync())
                        return;
                }
                catch { }
            }

            await Task.Delay(300);
        }

        throw new TimeoutException("Login did not reach a visible 'logged-in' signal. Check artifacts.");
    }

    private static string RequireEnv(string key) =>
        Environment.GetEnvironmentVariable(key) ?? throw new Exception($"Missing env var: {key}");
}
