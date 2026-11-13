"use strict";
const fs = require("node:fs");
const { chromium } = require("playwright");

(async () => {
    const executablePath = process.env.PORTAL_BROWSER_EXECUTABLE;
    if (executablePath && !fs.existsSync(executablePath)) {
        throw new Error(`Missing browser executable: ${executablePath}`);
    }
    let browser;
    try {
        // Use exactly the same browser selection as the complete browser suite.
        browser = await chromium.launch({ headless: true, ...(executablePath ? { executablePath } : { channel: "chromium" }) });
        console.log(`Chromium: ${browser.version()} (${executablePath || "Playwright-managed installation"})`);
    } catch (error) {
        if (/Executable doesn't exist|executable doesn't exist|ENOENT/.test(error.message)) {
            console.error("Missing Chromium executable. Run: npx --no-install playwright install chromium");
        } else if (/shared libraries|Host system is missing dependencies|Missing libraries|lib\S+\.so/.test(error.message)) {
            console.error("Missing Chromium runtime libraries/fonts. Repair the standardized runner image with Playwright's install-deps chromium prerequisite.");
        } else {
            console.error("Chromium infrastructure launch failed before synthetic application startup.");
        }
        throw error;
    } finally {
        if (browser) await browser.close();
    }
})().catch(error => { console.error(error); process.exitCode = 1; });
