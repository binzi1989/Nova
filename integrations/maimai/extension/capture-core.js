(function (root) {
  "use strict";

  const NAVIGATION = new Set([
    "首页", "社区", "招聘", "企业号", "商业服务", "品牌号", "消息", "聊天", "人脉",
    "职言", "机会", "我的", "登录", "注册", "脉脉", "关注", "推荐", "热榜", "看点评",
    "理财", "情感", "好友", "返回"
  ]);

  function clean(value, max = 500) {
    const text = String(value || "").replace(/\s+/g, " ").trim();
    return text.length <= max ? text : text.slice(0, max);
  }

  function linesFrom(text) {
    return String(text || "")
      .split(/\r?\n/)
      .map((line) => clean(line, 300))
      .filter(Boolean);
  }

  function labeled(lines, labels) {
    const expression = new RegExp(`^(?:${labels.join("|")})[：:]\\s*(.+)$`, "i");
    for (const line of lines) {
      const match = line.match(expression);
      if (match && match[1]) return clean(match[1], 160);
    }
    return "";
  }

  function parseVisibleText(text, pageTitle = "", heading = "") {
    const lines = linesFrom(text);
    const titleName = clean(String(pageTitle).split(/[\-_|｜]/)[0], 100);
    const displayName = clean(heading, 100)
      || (titleName !== "脉脉" && !NAVIGATION.has(titleName) ? titleName : "");
    const skillsLine = labeled(lines, ["技能", "擅长", "专业技能"]);
    return {
      displayName,
      currentTitle: labeled(lines, ["当前职位", "现任", "职位", "职业"]),
      currentCompany: labeled(lines, ["当前公司", "公司", "任职公司"]),
      location: labeled(lines, ["所在地", "地区", "城市"]),
      publicSummary: labeled(lines, ["简介", "个人简介", "职业简介"]),
      visibleSkills: skillsLine
        ? skillsLine.split(/[、,，/|]/).map((item) => clean(item, 80)).filter(Boolean).slice(0, 30)
        : [],
      evidenceText: clean(lines.join("\n"), 8000)
    };
  }

  function detectPageType(pathname) {
    const path = String(pathname || "").toLowerCase();
    if (/^\/community\/feed-detail(?:\/|$)/.test(path)) return "community-post-author";
    if (/(?:^|\/)(?:contact\/detail|profile)(?:\/|$)/.test(path)) return "person-profile";
    return "unsupported";
  }

  function parseCommunityFeed(text) {
    const lines = linesFrom(text);
    const combinedMetadataPattern = /^(\d{1,2}-\d{1,2})\s*[·・]\s*(.+)$/;
    const splitDatePattern = /^\d{1,2}-\d{1,2}\s*[·・]?\s*$/;
    let metadataIndex = lines.findIndex((line) => combinedMetadataPattern.test(line));
    let company = "";
    let bodyStart = metadataIndex + 1;
    if (metadataIndex >= 1) {
      company = lines[metadataIndex].match(combinedMetadataPattern)?.[2] || "";
    } else {
      metadataIndex = lines.findIndex((line, index) =>
        index > 0
        && splitDatePattern.test(line)
        && Boolean(lines[index + 1])
        && !NAVIGATION.has(lines[index + 1])
      );
      if (metadataIndex >= 1) {
        company = lines[metadataIndex + 1];
        bodyStart = metadataIndex + 2;
      }
    }
    if (metadataIndex < 1) {
      throw new Error("没有找到帖子作者信息，请确认当前是完整的帖子详情页。");
    }
    const displayName = lines[metadataIndex - 1];
    if (!displayName || NAVIGATION.has(displayName) || displayName.length > 100) {
      throw new Error("无法可靠识别帖子作者，已停止读取，避免保存错误对象。");
    }

    const locationLine = lines.find((line) => /^发布于\s*/.test(line));
    const endIndex = locationLine ? lines.indexOf(locationLine) : lines.length;
    const postLines = lines.slice(bodyStart, endIndex).filter((line) =>
      !["评论", "赞", "分享", "收藏"].includes(line)
    );
    if (!postLines.length) {
      throw new Error("帖子正文为空，请等待页面加载完成后重试。");
    }

    return {
      sourceType: "community-post-author",
      displayName: clean(displayName, 100),
      currentTitle: "",
      currentCompany: clean(company, 160),
      location: clean(locationLine?.replace(/^发布于\s*/, ""), 100),
      publicSummary: clean(postLines.join("\n"), 2000),
      visibleSkills: [],
      evidenceText: clean(lines.slice(metadataIndex - 1, endIndex).join("\n"), 8000)
    };
  }

  function scanDocument() {
    const host = location.hostname.toLowerCase();
    if (!(host === "maimai.cn" || host.endsWith(".maimai.cn")) || location.protocol !== "https:") {
      throw new Error("当前不是 HTTPS 脉脉页面。");
    }
    const pageType = detectPageType(location.pathname);
    if (pageType === "unsupported") {
      throw new Error("当前页面没有明确的单一对象。请打开个人主页或社区帖子详情页后再读取。");
    }
    const visibleText = document.body ? document.body.innerText : "";
    if (clean(visibleText).length < 20) {
      throw new Error("当前页面没有足够的可见内容，请等待页面加载完成。");
    }
    const heading = document.querySelector("h1")?.innerText || "";
    const parsed = pageType === "community-post-author"
      ? parseCommunityFeed(visibleText)
      : { sourceType: "person-profile", ...parseVisibleText(visibleText, document.title, heading) };
    if (!parsed.displayName) {
      throw new Error("无法可靠识别姓名/展示名，已停止读取，避免保存错误对象。");
    }

    const tags = Array.from(document.querySelectorAll('[class*="skill"], [class*="tag"]'))
      .map((element) => clean(element.innerText, 80))
      .filter((value) => value && value.length <= 80)
      .slice(0, 30);
    if (pageType === "person-profile" && tags.length) {
      parsed.visibleSkills = [...new Set([...parsed.visibleSkills, ...tags])].slice(0, 30);
    }

    return {
      ...parsed,
      sourceUrl: location.href,
      pageTitle: clean(document.title, 200)
    };
  }

  root.NovaMaimaiCapture = {
    clean,
    linesFrom,
    detectPageType,
    parseVisibleText,
    parseCommunityFeed,
    scanDocument
  };
})(globalThis);
