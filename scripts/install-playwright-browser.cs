// Installs the Chromium that ConfigPageBrowserTests drives, for the Playwright version pinned in
// Directory.Packages.props (this file names the package and takes its version from there, so a
// bump moves both). Run from CI before the suite:
//
//   PLAYWRIGHT_BROWSERS_PATH=<dir> dotnet run --file scripts/install-playwright-browser.cs
//
// The browser goes where PLAYWRIGHT_BROWSERS_PATH says, which CI points at the job's temporary
// directory: nothing is installed on the runner itself. Headless shell only, which is all the
// tests launch and a fraction of the download.
#:package Microsoft.Playwright
#:property PublishAot=false

return Microsoft.Playwright.Program.Main(["install", "--only-shell", "chromium"]);
