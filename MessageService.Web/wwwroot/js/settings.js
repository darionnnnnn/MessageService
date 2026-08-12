(() => {
    'use strict';

    const els = {};
    let groupsCache = [];
    // 設定 modal 關閉時，若這次開啟期間有成功寫入任何變更，就通知聊天畫面重新整理
    // （改名稱顯示模式、加關鍵字規則等都會影響目前開著的對話內容）
    let settingsDirty = false;
    let dataLoaded = false;

    function $(id) {
        return document.getElementById(id);
    }

    async function fetchJson(url, options) {
        const response = await fetch(url, options);
        if (!response.ok) {
            throw new Error(`HTTP ${response.status} for ${url}`);
        }
        const text = await response.text();
        return text ? JSON.parse(text) : null;
    }

    function showToast(message, isError) {
        const toast = document.createElement('div');
        toast.className = 'toast align-items-center text-bg-' + (isError ? 'danger' : 'success') + ' border-0';
        toast.setAttribute('role', 'alert');

        const flex = document.createElement('div');
        flex.className = 'd-flex';
        const body = document.createElement('div');
        body.className = 'toast-body';
        body.textContent = message;
        flex.appendChild(body);
        toast.appendChild(flex);

        els.toastContainer.appendChild(toast);
        const instance = bootstrap.Toast.getOrCreateInstance(toast, { delay: 2500 });
        instance.show();
        toast.addEventListener('hidden.bs.toast', () => toast.remove());
    }

    function groupDisplayName(groupId) {
        const group = groupsCache.find(g => g.groupId === groupId);
        return group ? group.displayName : groupId;
    }

    // === 關鍵字遮蔽 ===

    function renderKeywordRow(keyword) {
        const tr = document.createElement('tr');

        const keywordTd = document.createElement('td');
        keywordTd.textContent = keyword.keyword;
        tr.appendChild(keywordTd);

        const replacementTd = document.createElement('td');
        replacementTd.textContent = keyword.replacement ? `自訂：${keyword.replacement}` : '預設（等長 *）';
        tr.appendChild(replacementTd);

        const scopeTd = document.createElement('td');
        scopeTd.textContent = keyword.applyToAllGroups
            ? '全部群組'
            : (keyword.groupIds.map(groupDisplayName).join('、') || '（未指定）');
        tr.appendChild(scopeTd);

        const actionTd = document.createElement('td');
        const deleteBtn = document.createElement('button');
        deleteBtn.type = 'button';
        deleteBtn.className = 'btn btn-outline-danger btn-sm';
        deleteBtn.textContent = '刪除';
        deleteBtn.addEventListener('click', () => deleteKeyword(keyword.id));
        actionTd.appendChild(deleteBtn);
        tr.appendChild(actionTd);

        return tr;
    }

    async function loadKeywords() {
        const keywords = await fetchJson('/api/settings/keywords');
        els.keywordTbody.innerHTML = '';
        for (const keyword of keywords) {
            els.keywordTbody.appendChild(renderKeywordRow(keyword));
        }
    }

    async function deleteKeyword(id) {
        try {
            await fetchJson(`/api/settings/keywords/${id}`, { method: 'DELETE' });
            settingsDirty = true;
            showToast('已刪除規則');
            await loadKeywords();
        } catch {
            showToast('刪除失敗', true);
        }
    }

    function renderScopeCheckboxes() {
        els.scopeGroupCheckboxes.innerHTML = '';
        for (const group of groupsCache) {
            const wrap = document.createElement('div');
            wrap.className = 'form-check';

            const input = document.createElement('input');
            input.className = 'form-check-input';
            input.type = 'checkbox';
            input.value = group.groupId;
            input.id = `scope-group-${group.groupId}`;

            const label = document.createElement('label');
            label.className = 'form-check-label';
            label.setAttribute('for', input.id);
            label.textContent = group.displayName;

            wrap.appendChild(input);
            wrap.appendChild(label);
            els.scopeGroupCheckboxes.appendChild(wrap);
        }
    }

    async function handleKeywordSubmit(event) {
        event.preventDefault();
        const keyword = els.keywordInput.value.trim();
        if (!keyword) {
            return;
        }

        const replacement = els.replacementCustom.checked ? els.replacementInput.value.trim() || null : null;
        const applyToAllGroups = els.scopeAll.checked;
        const groupIds = applyToAllGroups
            ? []
            : Array.from(els.scopeGroupCheckboxes.querySelectorAll('input:checked')).map(i => i.value);

        try {
            await fetchJson('/api/settings/keywords', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ keyword, replacement, applyToAllGroups, groupIds })
            });
            settingsDirty = true;
            showToast('已新增規則');
            els.keywordForm.reset();
            els.replacementInput.disabled = true;
            els.scopeGroupCheckboxes.classList.add('d-none');
            await loadKeywords();
        } catch {
            showToast('新增失敗', true);
        }
    }

    // === 名稱顯示 ===

    async function loadDisplaySettings() {
        const settings = await fetchJson('/api/settings/display');
        const radio = document.querySelector(`input[name="display-mode"][value="${settings.nameDisplayMode}"]`);
        if (radio) {
            radio.checked = true;
        }
        toggleAliasEditor();
    }

    async function handleDisplayModeChange() {
        const mode = document.querySelector('input[name="display-mode"]:checked').value;
        try {
            await fetchJson('/api/settings/display', {
                method: 'PUT',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ nameDisplayMode: mode })
            });
            settingsDirty = true;
            showToast('顯示設定已更新');
        } catch {
            showToast('更新失敗', true);
        }
        toggleAliasEditor();
    }

    function toggleAliasEditor() {
        const mode = document.querySelector('input[name="display-mode"]:checked')?.value;
        els.aliasEditor.classList.toggle('d-none', mode !== 'CustomAlias');
        if (mode === 'CustomAlias') {
            loadAliasEditor();
        }
    }

    async function loadAliasEditor() {
        const groupId = els.aliasGroupFilter.value;
        const url = groupId ? `/api/users?groupId=${encodeURIComponent(groupId)}` : '/api/users';
        const [users, aliases] = await Promise.all([
            fetchJson(url),
            fetchJson('/api/settings/aliases')
        ]);
        const aliasMap = new Map(aliases.map(a => [a.userId, a.alias]));

        els.aliasTbody.innerHTML = '';
        for (const user of users) {
            const tr = document.createElement('tr');

            const nameTd = document.createElement('td');
            nameTd.textContent = user.displayName;
            nameTd.title = user.userId;
            tr.appendChild(nameTd);

            const aliasTd = document.createElement('td');
            const input = document.createElement('input');
            input.type = 'text';
            input.className = 'form-control form-control-sm';
            input.value = aliasMap.get(user.userId) || '';
            aliasTd.appendChild(input);
            tr.appendChild(aliasTd);

            const actionTd = document.createElement('td');
            const saveBtn = document.createElement('button');
            saveBtn.type = 'button';
            saveBtn.className = 'btn btn-outline-primary btn-sm';
            saveBtn.textContent = '儲存';
            saveBtn.addEventListener('click', () => saveAlias(user.userId, input.value.trim()));
            actionTd.appendChild(saveBtn);
            tr.appendChild(actionTd);

            els.aliasTbody.appendChild(tr);
        }
    }

    async function saveAlias(userId, alias) {
        try {
            if (alias) {
                await fetchJson(`/api/settings/aliases/${encodeURIComponent(userId)}`, {
                    method: 'PUT',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({ alias })
                });
            } else {
                // 清空別名等於刪除；本來就沒有別名時會回 404，那也是想要的結果，
                // 但其他錯誤碼要真的當成失敗，不能一律當成功
                const response = await fetch(`/api/settings/aliases/${encodeURIComponent(userId)}`, { method: 'DELETE' });
                if (!response.ok && response.status !== 404) {
                    throw new Error(`HTTP ${response.status}`);
                }
            }
            settingsDirty = true;
            showToast('別名已更新');
        } catch {
            showToast('更新失敗', true);
        }
    }

    // === 個資自動遮蔽（身分證／手機／市話／健保卡，格式比對，跟關鍵字規則是互補的兩層）===

    async function loadPiiMaskingSettings() {
        const settings = await fetchJson('/api/settings/pii-masking');
        els.piiNationalIdToggle.checked = settings.maskNationalId;
        els.piiMobileToggle.checked = settings.maskMobilePhone;
        els.piiLandlineToggle.checked = settings.maskLandline;
        els.piiNhiToggle.checked = settings.maskNhiCard;
    }

    async function handlePiiMaskingChange() {
        try {
            await fetchJson('/api/settings/pii-masking', {
                method: 'PUT',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({
                    maskNationalId: els.piiNationalIdToggle.checked,
                    maskMobilePhone: els.piiMobileToggle.checked,
                    maskLandline: els.piiLandlineToggle.checked,
                    maskNhiCard: els.piiNhiToggle.checked
                })
            });
            settingsDirty = true;
            showToast('個資遮蔽設定已更新');
        } catch {
            showToast('更新失敗', true);
        }
    }

    // === 訊息保留天數：不可逆操作，送出前要求二次確認（見 SettingsController 的說明）===

    async function loadRetentionSettings() {
        const settings = await fetchJson('/api/settings/retention');
        els.retentionDaysInput.value = settings.retentionDays;
    }

    async function handleRetentionSave() {
        const days = parseInt(els.retentionDaysInput.value, 10);
        if (!Number.isFinite(days) || days < 1 || days > 3650) {
            showToast('保留天數必須是 1～3650 之間的整數', true);
            return;
        }

        const confirmed = window.confirm(
            `確定要把保留天數改成 ${days} 天嗎？超過這個天數的訊息會在下次排程清除時永久刪除，無法復原。`);
        if (!confirmed) {
            return;
        }

        try {
            await fetchJson('/api/settings/retention', {
                method: 'PUT',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ retentionDays: days })
            });
            settingsDirty = true;
            showToast('保留天數已更新');
        } catch {
            showToast('更新失敗', true);
        }
    }

    // === 字體大小（跟對話頁共用同一份 localStorage key，這裡改了對話頁下次開啟也會吃到；
    //     對話頁的「中」檔＝這裡設定的數值，「小」「大」依既有比例跟著調整） ===

    const FONT_BASE_PX_STORAGE_KEY = 'chat-font-base-px';
    const DEFAULT_FONT_BASE_PX = 20;
    const FONT_BASE_PX_MIN = 12;
    const FONT_BASE_PX_MAX = 28;

    function loadFontBasePx() {
        let saved;
        try {
            saved = parseInt(localStorage.getItem(FONT_BASE_PX_STORAGE_KEY), 10);
        } catch {
            saved = NaN;
        }
        return Number.isFinite(saved) && saved >= FONT_BASE_PX_MIN && saved <= FONT_BASE_PX_MAX
            ? saved
            : DEFAULT_FONT_BASE_PX;
    }

    // 設在 document.documentElement 上，跟聊天畫面共用同一顆 CSS 變數——在這裡調字級，
    // 背後的聊天畫面（跟這個 modal 是同一個頁面）會即時跟著變，不用等下次重新整理
    function applyFontBasePx(px) {
        document.documentElement.style.setProperty('--font-base-px', `${px}px`);
    }

    function initFontBasePx() {
        // 頁面載入時 chat.js 已經套用過一次同一份設定，這裡只需要把輸入框的顯示值補上
        els.fontBasePxInput.value = loadFontBasePx();

        els.fontBasePxInput.addEventListener('change', () => {
            let value = parseInt(els.fontBasePxInput.value, 10);
            if (!Number.isFinite(value)) {
                value = DEFAULT_FONT_BASE_PX;
            }
            value = Math.min(FONT_BASE_PX_MAX, Math.max(FONT_BASE_PX_MIN, value));
            els.fontBasePxInput.value = value;
            applyFontBasePx(value);
            try {
                localStorage.setItem(FONT_BASE_PX_STORAGE_KEY, String(value));
            } catch {
                // localStorage 不可用（例如無痕模式）就只套用當次畫面，不用另外提示
            }
        });
    }

    // === 對話寬度（跟對話頁共用同一份 localStorage key 與 CSS 變數，比照上面的字體大小） ===

    const FULL_WIDTH_STORAGE_KEY = 'chat-full-width';

    // 未勾選一定要 removeProperty：預設寬度由樣式表的媒體查詢決定（桌面 2/3、手機 75%），
    // 留下 inline 值會把手機版也一起鎖死
    function applyChatWidth(full) {
        if (full) {
            document.documentElement.style.setProperty('--bubble-max-width', '100%');
        } else {
            document.documentElement.style.removeProperty('--bubble-max-width');
        }
    }

    function initFullWidthToggle() {
        // 頁面載入時 chat.js 已經套用過同一份設定，這裡只需要把勾選狀態補上
        let full = false;
        try {
            full = localStorage.getItem(FULL_WIDTH_STORAGE_KEY) === 'true';
        } catch {
            // localStorage 不可用就顯示未勾選
        }
        els.fullWidthToggle.checked = full;

        els.fullWidthToggle.addEventListener('change', () => {
            const checked = els.fullWidthToggle.checked;
            applyChatWidth(checked);
            try {
                localStorage.setItem(FULL_WIDTH_STORAGE_KEY, String(checked));
            } catch {
                // localStorage 不可用（例如無痕模式）就只套用當次畫面，不用另外提示
            }
        });
    }

    // === 初始化 ===
    // 設定現在是聊天頁裡的 modal，不再是獨立頁面：元素綁定跟不需要資料的監聽器在頁面
    // 載入時就做完，實際打 API 撈資料延後到第一次打開 modal 才做（shown.bs.modal），
    // 避免聊天頁一開就多打一輪只有設定頁才用得到的請求。

    function wireElements() {
        els.fontBasePxInput = $('font-base-px-input');
        els.fullWidthToggle = $('full-width-toggle');
        els.toastContainer = $('toast-container');
        els.keywordTbody = $('keyword-tbody');
        els.keywordForm = $('keyword-form');
        els.keywordInput = $('keyword-input');
        els.replacementDefault = $('replacement-default');
        els.replacementCustom = $('replacement-custom');
        els.replacementInput = $('replacement-input');
        els.scopeAll = $('scope-all');
        els.scopeSelected = $('scope-selected');
        els.scopeGroupCheckboxes = $('scope-group-checkboxes');
        els.aliasEditor = $('alias-editor');
        els.aliasGroupFilter = $('alias-group-filter');
        els.aliasTbody = $('alias-tbody');
        els.piiNationalIdToggle = $('pii-national-id-toggle');
        els.piiMobileToggle = $('pii-mobile-toggle');
        els.piiLandlineToggle = $('pii-landline-toggle');
        els.piiNhiToggle = $('pii-nhi-toggle');
        els.retentionDaysInput = $('retention-days-input');
        els.retentionSaveBtn = $('retention-save-btn');
        els.settingsModal = $('settings-modal');
        els.settingsModalBody = $('settings-modal-body');
    }

    function wireStaticListeners() {
        initFontBasePx();
        initFullWidthToggle();

        els.replacementCustom.addEventListener('change', () => { els.replacementInput.disabled = false; });
        els.replacementDefault.addEventListener('change', () => { els.replacementInput.disabled = true; });
        els.scopeSelected.addEventListener('change', () => els.scopeGroupCheckboxes.classList.remove('d-none'));
        els.scopeAll.addEventListener('change', () => els.scopeGroupCheckboxes.classList.add('d-none'));
        els.keywordForm.addEventListener('submit', handleKeywordSubmit);

        document.querySelectorAll('input[name="display-mode"]').forEach(
            radio => radio.addEventListener('change', handleDisplayModeChange));
        els.aliasGroupFilter.addEventListener('change', loadAliasEditor);

        for (const toggle of [els.piiNationalIdToggle, els.piiMobileToggle, els.piiLandlineToggle, els.piiNhiToggle]) {
            toggle.addEventListener('change', handlePiiMaskingChange);
        }
        els.retentionSaveBtn.addEventListener('click', handleRetentionSave);

        // 換分頁時把捲動位置歸零；不然上一個分頁捲很深時，切過去的新分頁會被卡在
        // 同一個捲動位置，內容被卡在畫面外
        for (const tabBtn of document.querySelectorAll('#settings-tabs [data-bs-toggle="tab"]')) {
            tabBtn.addEventListener('shown.bs.tab', () => {
                els.settingsModalBody.scrollTop = 0;
            });
        }

        els.settingsModal.addEventListener('shown.bs.modal', () => {
            if (!dataLoaded) {
                dataLoaded = true;
                loadInitialData().catch(() => {
                    // 失敗要把旗標放掉，下次重開 modal 才會重試；不然一次瞬斷就讓設定
                    // 永遠載不進來，只能重新整理整頁
                    dataLoaded = false;
                    showToast('載入設定失敗，請關閉後重試', true);
                });
            }
        });

        els.settingsModal.addEventListener('hidden.bs.modal', () => {
            if (settingsDirty) {
                document.dispatchEvent(new CustomEvent('messageservice:settings-changed'));
                settingsDirty = false;
            }
        });
    }

    async function loadInitialData() {
        groupsCache = await fetchJson('/api/groups');
        renderScopeCheckboxes();

        els.aliasGroupFilter.innerHTML = '';
        const allOption = document.createElement('option');
        allOption.value = '';
        allOption.textContent = '全部群組';
        els.aliasGroupFilter.appendChild(allOption);
        for (const group of groupsCache) {
            const opt = document.createElement('option');
            opt.value = group.groupId;
            opt.textContent = group.displayName;
            els.aliasGroupFilter.appendChild(opt);
        }

        await Promise.all([loadKeywords(), loadDisplaySettings(), loadPiiMaskingSettings(), loadRetentionSettings()]);
    }

    function init() {
        wireElements();
        wireStaticListeners();
    }

    document.addEventListener('DOMContentLoaded', init);
})();
