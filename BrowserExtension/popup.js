const statusBadge = document.getElementById("statusBadge");
const totalSpeedBadge = document.getElementById("totalSpeedBadge");
const downloadList = document.getElementById("downloadList");
const chkAutoCapture = document.getElementById("chkAutoCapture");
const selMinSize = document.getElementById("selMinSize");
const txtQuickUrl = document.getElementById("txtQuickUrl");
const btnSend = document.getElementById("btnSend");
const sendFeedback = document.getElementById("sendFeedback");

// Load saved settings
chrome.storage.sync.get({ autoCapture: true, minSizeMB: 1 }, (items) => {
  chkAutoCapture.checked = Boolean(items.autoCapture);
  selMinSize.value = String(items.minSizeMB ?? 1);
});

chkAutoCapture.addEventListener("change", () => {
  chrome.storage.sync.set({ autoCapture: chkAutoCapture.checked });
});

selMinSize.addEventListener("change", () => {
  chrome.storage.sync.set({ minSizeMB: Number(selMinSize.value) });
});

function escapeHtml(str) {
  return String(str ?? "")
    .replaceAll("&", "&amp;")
    .replaceAll("<", "&lt;")
    .replaceAll(">", "&gt;")
    .replaceAll('"', "&quot;");
}

function renderDownloads(downloads) {
  if (!downloads || downloads.length === 0) {
    downloadList.innerHTML = `<div class="empty-state">Chưa có mục tải nào.</div>`;
    return;
  }

  downloadList.innerHTML = downloads
    .slice(0, 5)
    .map((d) => {
      const pct = Math.max(0, Math.min(100, Number(d.progress) || 0));
      const isDone = d.status === "Completed" || pct >= 100;
      const actionBtn = d.canPause
        ? `<button class="dl-btn" data-id="${escapeHtml(d.id)}" data-cmd="pause" title="Tạm dừng">⏸</button>`
        : d.canResume
        ? `<button class="dl-btn" data-id="${escapeHtml(d.id)}" data-cmd="resume" title="Tiếp tục">▶</button>`
        : "";

      const rightMeta = isDone
        ? `Hoàn thành`
        : `${escapeHtml(d.speed)} • ${escapeHtml(d.eta)}`;

      return `
        <div class="dl-item">
          <div class="dl-top">
            <div class="dl-name" title="${escapeHtml(d.name)}">${escapeHtml(d.name)}</div>
            ${actionBtn}
          </div>
          <div class="progress-track">
            <div class="progress-fill ${isDone ? "done" : ""}" style="width: ${pct}%"></div>
          </div>
          <div class="dl-meta">
            <span>${pct.toFixed(1)}% • ${escapeHtml(d.size)}</span>
            <span>${rightMeta}</span>
          </div>
        </div>
      `;
    })
    .join("");

  downloadList.querySelectorAll(".dl-btn").forEach((btn) => {
    btn.addEventListener("click", () => {
      const id = btn.getAttribute("data-id");
      const cmd = btn.getAttribute("data-cmd");
      chrome.runtime.sendMessage({ action: "downloadAction", id, cmd }, () => {
        refreshDownloads();
      });
    });
  });
}

function refreshDownloads() {
  chrome.runtime.sendMessage({ action: "getDownloads" }, (res) => {
    if (res?.connected && res.data) {
      statusBadge.textContent = "🟢 Đã kết nối";
      statusBadge.className = "badge badge-online";
      totalSpeedBadge.textContent = `⬇ ${res.data.totalSpeedText || "0 B/s"}`;
      renderDownloads(res.data.downloads || []);
    } else {
      statusBadge.textContent = "🔴 Chưa mở App";
      statusBadge.className = "badge badge-offline";
      totalSpeedBadge.textContent = "⬇ 0 B/s";
      downloadList.innerHTML = `<div class="empty-state">Hãy mở TurboGet.exe để xem tiến trình tải.</div>`;
    }
  });
}

btnSend.addEventListener("click", () => {
  const url = txtQuickUrl.value.trim();
  if (!url) {
    sendFeedback.textContent = "Vui lòng nhập URL hoặc link Magnet.";
    sendFeedback.className = "feedback err";
    return;
  }

  btnSend.disabled = true;
  sendFeedback.textContent = "Đang gửi sang TurboGet...";
  sendFeedback.className = "feedback";

  chrome.runtime.sendMessage({ action: "sendUrl", url }, (res) => {
    btnSend.disabled = false;
    if (res?.ok) {
      sendFeedback.textContent = `Đã thêm: ${res.data?.name || "thành công"}`;
      sendFeedback.className = "feedback ok";
      txtQuickUrl.value = "";
      refreshDownloads();
    } else {
      sendFeedback.textContent = "Không kết nối được. Hãy mở TurboGet.exe trước.";
      sendFeedback.className = "feedback err";
    }
  });
});

txtQuickUrl.addEventListener("keydown", (e) => {
  if (e.key === "Enter") btnSend.click();
});

refreshDownloads();
setInterval(refreshDownloads, 1000);
