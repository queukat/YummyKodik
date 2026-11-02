"use strict";

const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

const html = fs.readFileSync(path.resolve(__dirname, "../YummyKodik/Web/config.html"), "utf8");
const source = html.match(/<script type="text\/javascript">([\s\S]*?)<\/script>/)[1];

function harness(culture = "ru", initial = {}) {
    let cfg = structuredClone(initial);
    const writes = [];
    const toasts = [];
    const elements = new Map();
    const textElements = [];
    const observers = [];
    const requests = [];
    let authResult = { Success: true, Code: "connected", UserId: 42 };
    function element(attributes = {}) {
        const handlers = new Map();
        return {
            value: "", checked: false, disabled: false, required: false, textContent: "",
            children: [],
            replaceChildren() { this.children = []; this.value = ""; },
            appendChild(child) { this.children.push(child); },
            getAttribute: name => attributes[name],
            setAttribute: (name, value) => { attributes[name] = value; },
            addEventListener: (name, handler) => handlers.set(name, handler),
            dispatch: name => handlers.get(name)?.({ preventDefault() {} }),
            focus() { this.focused = true; }
        };
    }
    for (const match of html.matchAll(/<[^>]+\bid="([^"]+)"[^>]*>/g)) {
        elements.set(match[1], element());
    }
    for (const match of html.matchAll(/\bdata-i18n="([^"]+)"/g)) {
        textElements.push(element({ "data-i18n": match[1] }));
    }
    elements.get("YummyKodikConfigPage").querySelectorAll = () => textElements;
    const cancel = element();
    const document = {
        documentElement: { lang: culture },
        createElement: () => element(),
        getElementById: id => {
            assert.ok(elements.has(id), `Missing page control: ${id}`);
            return elements.get(id);
        },
        querySelector: () => cancel
    };
    vm.runInNewContext(source, {
        document,
        MutationObserver: class {
            constructor(callback) { this.callback = callback; observers.push(this); }
            observe() { this.connected = true; }
            disconnect() { this.connected = false; }
        },
        ApiClient: {
            getUrl: value => value,
            async ajax(request) { requests.push(request); return authResult; },
            async getPluginConfiguration() { return structuredClone(cfg); },
            async updatePluginConfiguration(id, value) {
                writes.push(structuredClone(value));
                cfg = structuredClone(value);
                return {};
            }
        },
        Dashboard: {
            showLoadingMsg() {}, hideLoadingMsg() {}, processPluginConfigurationUpdateResult() {},
            showToast({ text }) { toasts.push(text); }, close() {}
        },
        console: { error() {} }
    });
    return {
        elements, writes, toasts, requests,
        setAuthResult(value) { authResult = value; },
        signIn: () => elements.get("BtnYummySignIn").dispatch("click"),
        tokenSignIn: () => elements.get("BtnYummyToken").dispatch("click"),
        text: key => textElements.find(node => node.getAttribute("data-i18n") === key)?.textContent,
        show: () => elements.get("YummyKodikConfigPage").dispatch("pageshow"),
        submit: () => elements.get("YummyKodikConfigurationForm").dispatch("submit"),
        language(value) {
            document.documentElement.lang = value;
            observers.filter(observer => observer.connected).forEach(observer => observer.callback());
        }
    };
}

(async () => {
    const account = harness("ru", { YummyLogin: "saved@example.test", YummyPassword: "stored-password", YummyAccessToken: "stored-token", YummyAccountStatus: "invalid_credentials" });
    await account.show();
    assert.equal(account.elements.get("yummyPassword").value, "");
    assert.equal(account.elements.get("yummyAccessToken").value, "");
    assert.match(account.elements.get("yummyAccountStatus").textContent, /отклонил/);
    account.elements.get("yummyPassword").value = "new-password";
    account.elements.get("outputRootPath").value = "D:\\unsaved";
    await account.submit();
    assert.equal(account.writes.length, 0, "Account changes require explicit sign-in, not ordinary Save");
    await account.signIn();
    assert.equal(JSON.parse(account.requests[0].data).Password, "new-password");
    assert.equal(account.requests[0].type, "POST");
    assert.equal(account.elements.get("yummyPassword").value, "");
    assert.equal(account.elements.get("outputRootPath").value, "D:\\unsaved", "Signing in must preserve unrelated unsaved fields");
    assert.equal(account.writes.length, 0, "Sign-in must not save the full form");
    assert.equal(account.elements.get("yummyUserId").value, 42);
    account.setAuthResult({ Success: false, Code: "captcha_required" });
    account.elements.get("yummyAccessToken").value = "candidate";
    await account.tokenSignIn();
    assert.match(account.elements.get("yummyAccountStatus").textContent, /hCaptcha/);
    assert.equal(account.elements.get("BtnYummyToken").disabled, false);
    assert.equal(JSON.parse(account.requests[1].data).UseToken, true);

    const legacy = harness("ru-RU", {
        OutputRootPath: "D:\\Anime", YummyUserListId: 0, Slugs: ["frieren"],
        MinimumLogLevel: "Warning", UnrelatedFutureSetting: { preserve: true }
    });
    await legacy.show();
    assert.equal(legacy.text("save"), "Сохранить");
    assert.deepEqual(legacy.elements.get("yummyUserListId").children.map(option => [option.value, option.textContent]), [
        ["0", "Смотрю"], ["1", "В Планах"], ["2", "Просмотрено"],
        ["5", "Отложено"], ["3", "Брошено"], ["4", "Любимые"]
    ]);
    assert.equal(legacy.elements.get("secondOutputRootPath").disabled, true);
    assert.equal(legacy.elements.get("secondOutputRootPath").required, false);
    await legacy.submit();
    assert.equal(legacy.writes.length, 1);
    assert.equal(legacy.writes[0].UseSecondUserListSubscription, false);
    assert.equal(legacy.writes[0].SecondOutputRootPath, "");
    assert.deepEqual(legacy.writes[0].UnrelatedFutureSetting, { preserve: true });
    assert.deepEqual(Array.from(legacy.writes[0].Slugs), ["frieren"]);
    assert.equal(legacy.writes[0].YummyUserListId, 0);

    legacy.language("en-US");
    assert.equal(legacy.text("save"), "Save");
    assert.equal(legacy.text("secondOutputRoot"), "Second library folder");
    assert.equal(legacy.elements.get("minimumLogLevel").value, "Warning");
    legacy.elements.get("useSecondUserListSubscription").checked = true;
    legacy.elements.get("useSecondUserListSubscription").dispatch("change");
    assert.equal(legacy.elements.get("secondOutputRootPath").disabled, false);
    assert.equal(legacy.elements.get("secondOutputRootPath").required, true);
    legacy.elements.get("secondOutputRootPath").value = "   ";
    await legacy.submit();
    assert.equal(legacy.writes.length, 1, "An enabled second list cannot save without its folder");
    assert.equal(legacy.toasts.at(-1), "Enter a folder for the second list.");
    assert.equal(legacy.elements.get("secondOutputRootPath").focused, true);

    legacy.elements.get("secondOutputRootPath").value = "  D:\\Completed  ";
    legacy.elements.get("secondYummyUserListId").value = "2";
    await legacy.submit();
    assert.equal(legacy.writes.at(-1).UseSecondUserListSubscription, true);
    assert.equal(legacy.writes.at(-1).SecondYummyUserListId, 2);
    assert.equal(legacy.writes.at(-1).SecondOutputRootPath, "D:\\Completed");
    assert.equal(legacy.toasts.at(-1), "Settings saved");
    assert.equal(legacy.elements.get("secondYummyUserListId").value, "2");
    assert.equal(legacy.elements.get("secondYummyUserListId").children[2].textContent, "Completed");
    legacy.language("de-DE");
    assert.equal(legacy.text("save"), "Сохранить", "Unsupported Jellyfin languages fall back to Russian");
    legacy.language("");
    assert.equal(legacy.text("title"), "Настройки YummyKodik");
    legacy.elements.get("YummyKodikConfigPage").dispatch("pagehide");
    legacy.language("en");
    assert.equal(legacy.text("save"), "Сохранить", "Hidden pages stop observing language changes");
    await legacy.show();
    assert.equal(legacy.text("save"), "Save", "Reopening uses the current interface language");
    const future = harness("ru", { YummyUserListId: 17, SecondYummyUserListId: 18 });
    await future.show();
    assert.equal(future.elements.get("yummyUserListId").value, "17");
    assert.equal(future.elements.get("yummyUserListId").children.at(-1).textContent, "Сохранённый список (ID) 17");
    future.language("en-GB");
    assert.equal(future.elements.get("yummyUserListId").value, "17");
    assert.equal(future.elements.get("secondYummyUserListId").value, "18");
    assert.equal(future.elements.get("yummyUserListId").children.at(-1).textContent, "Saved list (ID) 17");
    await future.submit();
    assert.equal(future.writes[0].YummyUserListId, 17);
    assert.equal(future.writes[0].SecondYummyUserListId, 18);
    for (const culture of ["en", "EN-gb", "en_US", "ru", "ru-RU", "fr"]) {
        const page = harness(culture);
        await page.show();
        for (const match of html.matchAll(/\bdata-i18n="([^"]+)"/g)) {
            assert.ok(page.text(match[1]), `Missing ${culture} translation: ${match[1]}`);
        }
    }
    console.log("Configuration page tests passed (localization, legacy roundtrip, second-list persistence and validation).");
})().catch(error => { console.error(error); process.exitCode = 1; });
