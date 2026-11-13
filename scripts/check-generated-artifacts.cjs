"use strict";

const { execFileSync } = require("node:child_process");
const path = require("node:path");

// Read the index so forced additions and files missing from disk are checked too.
let tracked;
try {
    tracked = execFileSync("git", ["ls-files", "--cached", "-z"], {
        cwd: path.resolve(__dirname, ".."),
        encoding: "utf8",
        stdio: ["ignore", "pipe", "pipe"]
    });
} catch (error) {
    console.error("Could not inspect tracked repository paths:", error.message);
    process.exit(1);
}

const generated = tracked.split("\0").filter(file =>
    /^docs\/[^/]*-screenshots\//.test(file) ||
    /^docs\/(?:[^/]+\/)*screenshots\//.test(file) ||
    /^artifacts\//.test(file));

if (generated.length) {
    console.error("Generated verification artifacts must not be tracked:");
    for (const file of generated) {
        console.error("  " + JSON.stringify(file));
    }
    console.error("Keep generated output under ignored artifacts/ and upload CI evidence when needed. See AGENTS.md.");
    process.exitCode = 1;
} else {
    console.log("PASS: no generated verification artifact directories are tracked.");
}
