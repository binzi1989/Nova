"use strict";

const HOST = "ai.nova.maimai";
let snapshot = null;

const byId = (id) => document.getElementById(id);
const fields = ["displayName", "currentTitle", "currentCompany", "location", "publicSummary"];

function setStatus(message, kind = "neutral") {
  const status = byId("status");
  status.textContent = message;
  status.className = `status ${kind}`;
}

function nativeMessage(message) {
  return new Promise((resolve, reject) => {
    chrome.runtime.sendNativeMessage(HOST, message, (response) => {
      if (chrome.runtime.lastError) {
        reject(new Error(chrome.runtime.lastError.message));
        return;
      }
      if (!response?.ok) {
        reject(new Error(response?.error || "本地连接器未返回有效结果。"));
        return;
      }
      resolve(response);
    });
  });
}

async function scanPage() {
  byId("save").disabled = true;
  setStatus("正在读取当前页面的可见内容…");
  const [tab] = await chrome.tabs.query({ active: true, currentWindow: true });
  if (!tab?.id || !/^https:\/\/([a-z0-9-]+\.)*maimai\.cn(?:\/|$)/i.test(tab.url || "")) {
    throw new Error("请先打开一个 HTTPS 脉脉页面，再点击扩展。 ");
  }
  await chrome.scripting.executeScript({ target: { tabId: tab.id }, files: ["capture-core.js"] });
  const results = await chrome.scripting.executeScript({
    target: { tabId: tab.id },
    func: () => globalThis.NovaMaimaiCapture.scanDocument()
  });
  snapshot = results?.[0]?.result;
  if (!snapshot) throw new Error("未能读取当前页面，请刷新脉脉页面后重试。");
  for (const field of fields) byId(field).value = snapshot[field] || "";
  byId("visibleSkills").value = (snapshot.visibleSkills || []).join("，");
  byId("summaryLabel").firstChild.textContent = snapshot.sourceType === "community-post-author"
    ? "公开帖子摘要"
    : "公开简介";
  byId("save").disabled = false;
  const objectType = snapshot.sourceType === "community-post-author" ? "社区帖子作者" : "个人主页";
  setStatus(`已识别为${objectType}。请核对字段，勾选确认后保存。`, "good");
}

async function initialize() {
  try {
    await nativeMessage({ action: "ping" });
    const saved = await chrome.storage.local.get(["purpose", "retentionDays"]);
    if (saved.purpose) byId("purpose").value = saved.purpose;
    if (saved.retentionDays) byId("retentionDays").value = String(saved.retentionDays);
    await scanPage();
  } catch (error) {
    setStatus(`尚未就绪：${error.message}`, "bad");
  }
}

byId("scan").addEventListener("click", () => scanPage().catch((error) => setStatus(error.message, "bad")));
byId("profileForm").addEventListener("submit", async (event) => {
  event.preventDefault();
  if (!snapshot || !byId("confirm").checked) return;
  byId("save").disabled = true;
  setStatus("正在写入本地加密仓…");
  try {
    const payload = {
      ...snapshot,
      displayName: byId("displayName").value,
      currentTitle: byId("currentTitle").value,
      currentCompany: byId("currentCompany").value,
      location: byId("location").value,
      publicSummary: byId("publicSummary").value,
      visibleSkills: byId("visibleSkills").value.split(/[，,、]/).map((value) => value.trim()).filter(Boolean),
      purpose: byId("purpose").value,
      notes: byId("notes").value,
      retentionDays: Number(byId("retentionDays").value)
    };
    const response = await nativeMessage({ action: "capture", payload });
    await chrome.storage.local.set({ purpose: payload.purpose, retentionDays: payload.retentionDays });
    byId("confirm").checked = false;
    setStatus(`已保存：${response.profile.displayName}（ID ${response.profile.id}）`, "good");
  } catch (error) {
    setStatus(`保存失败：${error.message}`, "bad");
  } finally {
    byId("save").disabled = false;
  }
});

initialize();
