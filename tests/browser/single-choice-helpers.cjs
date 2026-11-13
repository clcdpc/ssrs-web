"use strict";

module.exports = (page, inputsReady) => {
    const single = prompt => page.locator(".single-select").filter({
        has: page.locator("label[id]").filter({ hasText: new RegExp("^" + prompt.replace(/[.*+?^${}()|[\]\\]/g, "\\$&") + "$") })
    });
    const openSingle = async prompt => {
        const group = single(prompt);
        await group.locator(".single-trigger").click();
        await group.getByRole("listbox").waitFor();
        await group.getByRole("listbox").getByRole("option").last().waitFor();
        return group;
    };
    const selectSingle = async (prompt, label) => {
        const group = await openSingle(prompt);
        await group.getByRole("searchbox").fill(label);
        await group.getByRole("option", { name: label, exact: true }).click();
        await inputsReady();
    };
    return { single, openSingle, selectSingle };
};
