"use strict";

const assert = require("node:assert/strict");
const { execFileSync, spawnSync } = require("node:child_process");
const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");
const test = require("node:test");

function repository(t) {
    const temporaryRoot = fs.realpathSync(os.tmpdir());
    const root = fs.mkdtempSync(path.join(temporaryRoot, "ssrs-artifact-guard-"));
    t.after(() => {
        assert.equal(path.dirname(root), temporaryRoot);
        assert.ok(path.basename(root).startsWith("ssrs-artifact-guard-"));
        fs.rmSync(root, { recursive: true, force: true });
    });
    // A caller's alternate index or Git hook environment must not escape this fixture.
    const environment = { ...process.env };
    for (const variable of ["GIT_DIR", "GIT_WORK_TREE", "GIT_COMMON_DIR", "GIT_INDEX_FILE", "GIT_OBJECT_DIRECTORY", "GIT_ALTERNATE_OBJECT_DIRECTORIES"]) {
        delete environment[variable];
    }
    const git = (...args) => execFileSync("git", args, { cwd: root, env: environment, stdio: "pipe" });
    const write = file => {
        fs.mkdirSync(path.dirname(path.join(root, file)), { recursive: true });
        fs.writeFileSync(path.join(root, file), "synthetic path fixture\n");
    };
    git("init", "--quiet");
    fs.mkdirSync(path.join(root, "scripts"));
    fs.copyFileSync(__filename.replace(".test.cjs", ".cjs"), path.join(root, "scripts", "check-generated-artifacts.cjs"));
    fs.copyFileSync(path.join(__dirname, "..", ".gitignore"), path.join(root, ".gitignore"));
    const check = () => spawnSync(process.execPath, [path.join(root, "scripts", "check-generated-artifacts.cjs")], {
        // The check must use its repository root even when called from a subdirectory.
        cwd: path.join(root, "scripts"), env: environment, encoding: "utf8"
    });
    return { root, git, write, check };
}

test("allows source images, branding fixtures and permanent documentation while ignoring generated output", t => {
    const { git, write, check } = repository(t);
    const assets = [
        "src/SsrsPortal.Mock/Branding/new-fixture.png",
        "src/SsrsPortal/wwwroot/images/logo.webp",
        "docs/images/current-workflow.png"
    ];
    const generated = [
        "docs/example-screenshots/browser.png",
        "docs/nested/task/screenshots/browser.jpg",
        "artifacts/browser/evidence.gif"
    ];
    for (const file of [...assets, ...generated]) { write(file); }
    git("add", ".");
    const tracked = git("ls-files", "-z").toString().split("\0");
    for (const asset of assets) { assert.ok(tracked.includes(asset)); }
    for (const file of generated) { assert.ok(!tracked.includes(file)); }
    const result = check();
    assert.equal(result.status, 0, result.stderr);
    assert.match(result.stdout, /PASS/);
});

test("rejects forced tracked output of any type and reports paths absent from disk", t => {
    const { root, git, write, check } = repository(t);
    const generated = [
        "docs/example-screenshots/browser.png",
        "docs/example-screenshots/timing.json",
        "docs/screenshots/top-level.gif",
        "docs/nested/task/screenshots/browser recording.webm",
        "artifacts/browser/forced.webp"
    ];
    for (const file of generated) { write(file); }
    // Force-add only synthetic fixtures in this disposable repository to test bypasses.
    git("add", "-f", "--", ...generated);
    fs.unlinkSync(path.join(root, generated[0]));
    const rejected = check();
    assert.equal(rejected.status, 1, rejected.stderr);
    for (const file of generated) { assert.ok(rejected.stderr.includes(JSON.stringify(file)), rejected.stderr); }
    git("rm", "--cached", "--", ...generated);
    const cleaned = check();
    assert.equal(cleaned.status, 0, cleaned.stderr);
});
