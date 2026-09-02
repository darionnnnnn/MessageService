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

    // toast 的建構邏輯只在 chat.js 寫一份（容器在聊天頁層級，設定 modal 內外都看得到）。
    // 這裡刻意不加「找不到就靜默略過」的保護——真的沒載到 chat.js 是頁面壞了，要當場炸出來
    function showToast(message, isError) {
        window.messageServiceToast(message, isError);
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
        const keywords = await fetchJson('api/settings/keywords');
        els.keywordTbody.innerHTML = '';
        for (const keyword of keywords) {
            els.keywordTbody.appendChild(renderKeywordRow(keyword));
        }
    }

    async function deleteKeyword(id) {
        try {
            await fetchJson(`api/settings/keywords/${id}`, { method: 'DELETE' });
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
            await fetchJson('api/settings/keywords', {
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

    // === 訊息高亮（高亮關鍵字、高亮人員、顯示效果） ===

    const {
        buildHighlightGradient,
        hexToGlow,
        normalizeHexColor,
        loadHighlightFlow,
        loadHighlightColors,
        HIGHLIGHT_FLOW_STORAGE_KEY,
        HIGHLIGHT_COLORS_STORAGE_KEY,
        DEFAULT_HIGHLIGHT_COLORS,
        MAX_HIGHLIGHT_COLORS
    } = window.messageServiceHighlight;
    const MIN_HIGHLIGHT_COLORS = 1;
    const PRESET_HIGHLIGHT_COLORS = [
        { hex: '#06c755', name: '綠' },
        { hex: '#ffc53d', name: '黃' },
        { hex: '#ff6b57', name: '珊瑚紅' },
        { hex: '#a66cff', name: '紫' },
        { hex: '#ff8a3d', name: '橘' },
        { hex: '#00b8d9', name: '青' },
        { hex: '#f25ca2', name: '粉' },
        { hex: '#5b7fff', name: '靛' }
    ];

    let highlightColors = [...DEFAULT_HIGHLIGHT_COLORS];

    // --- 高亮關鍵字 ---

    function renderHighlightKeywordRow(keyword) {
        const tr = document.createElement('tr');

        const keywordTd = document.createElement('td');
        keywordTd.textContent = keyword.keyword;
        tr.appendChild(keywordTd);

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
        deleteBtn.addEventListener('click', () => deleteHighlightKeyword(keyword.id));
        actionTd.appendChild(deleteBtn);
        tr.appendChild(actionTd);

        return tr;
    }

    async function loadHighlightKeywords() {
        const keywords = await fetchJson('api/settings/highlight-keywords');
        els.highlightKeywordTbody.innerHTML = '';
        for (const keyword of keywords) {
            els.highlightKeywordTbody.appendChild(renderHighlightKeywordRow(keyword));
        }
    }

    async function deleteHighlightKeyword(id) {
        try {
            await fetchJson(`api/settings/highlight-keywords/${id}`, { method: 'DELETE' });
            settingsDirty = true;
            showToast('已刪除高亮關鍵字');
            await loadHighlightKeywords();
        } catch {
            showToast('刪除失敗', true);
        }
    }

    function renderHighlightScopeCheckboxes() {
        els.highlightScopeGroupCheckboxes.innerHTML = '';
        for (const group of groupsCache) {
            const wrap = document.createElement('div');
            wrap.className = 'form-check';

            const input = document.createElement('input');
            input.className = 'form-check-input';
            input.type = 'checkbox';
            input.value = group.groupId;
            input.id = `highlight-scope-group-${group.groupId}`;

            const label = document.createElement('label');
            label.className = 'form-check-label';
            label.setAttribute('for', input.id);
            label.textContent = group.displayName;

            wrap.appendChild(input);
            wrap.appendChild(label);
            els.highlightScopeGroupCheckboxes.appendChild(wrap);
        }
    }

    async function handleHighlightKeywordSubmit(event) {
        event.preventDefault();
        const keyword = els.highlightKeywordInput.value.trim();
        if (!keyword) {
            return;
        }

        const applyToAllGroups = els.highlightScopeAll.checked;
        const groupIds = applyToAllGroups
            ? []
            : Array.from(els.highlightScopeGroupCheckboxes.querySelectorAll('input:checked')).map(i => i.value);

        try {
            await fetchJson('api/settings/highlight-keywords', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ keyword, applyToAllGroups, groupIds })
            });
            settingsDirty = true;
            showToast('已新增高亮關鍵字');
            els.highlightKeywordForm.reset();
            els.highlightScopeGroupCheckboxes.classList.add('d-none');
            await loadHighlightKeywords();
        } catch {
            showToast('新增失敗', true);
        }
    }

    // --- 高亮人員 ---

    function renderHighlightUserRow(user) {
        const tr = document.createElement('tr');

        const nameTd = document.createElement('td');
        nameTd.textContent = user.displayName;
        tr.appendChild(nameTd);

        const scopeTd = document.createElement('td');
        scopeTd.textContent = user.groupId == null ? '全部群組' : (user.groupName || '（未知群組）');
        tr.appendChild(scopeTd);

        const actionTd = document.createElement('td');
        const removeBtn = document.createElement('button');
        removeBtn.type = 'button';
        removeBtn.className = 'btn btn-outline-danger btn-sm';
        removeBtn.textContent = '移除';
        removeBtn.addEventListener('click', () => deleteHighlightUser(user.id));
        actionTd.appendChild(removeBtn);
        tr.appendChild(actionTd);

        return tr;
    }

    async function loadHighlightUsers() {
        const users = await fetchJson('api/settings/highlight-users');
        els.highlightUsersEmpty.classList.toggle('d-none', Boolean(users && users.length > 0));
        els.highlightUserTbody.innerHTML = '';
        if (users) {
            for (const user of users) {
                els.highlightUserTbody.appendChild(renderHighlightUserRow(user));
            }
        }
    }

    async function deleteHighlightUser(id) {
        try {
            await fetchJson(`api/settings/highlight-users/${id}`, { method: 'DELETE' });
            settingsDirty = true;
            showToast('已移除人員高亮');
            await loadHighlightUsers();
        } catch {
            showToast('移除失敗', true);
        }
    }

    // --- 顯示效果（流動開關與顏色） ---

    function saveHighlightFlow(enabled) {
        try {
            localStorage.setItem(HIGHLIGHT_FLOW_STORAGE_KEY, enabled ? '1' : '0');
        } catch {
            // localStorage 不可用就只套用當次工作階段
        }
    }

    function saveHighlightColors(colors) {
        try {
            localStorage.setItem(HIGHLIGHT_COLORS_STORAGE_KEY, JSON.stringify(colors));
        } catch {
            // localStorage 不可用就只套用當次工作階段
        }
    }

    function updateHighlightPreview() {
        if (!els.highlightPreviewBubble) {
            return;
        }
        const gradient = buildHighlightGradient(highlightColors);
        els.highlightPreviewBubble.style.setProperty('--highlight-preview-gradient', gradient);
        els.highlightPreviewBubble.style.setProperty('--highlight-preview-glow', hexToGlow(highlightColors[0], 0.45));
        const flowEnabled = els.highlightFlowToggle.checked;
        els.highlightPreviewBubble.classList.toggle('flowing', flowEnabled);
    }

    function renderPresetSwatches() {
        els.highlightPresetColors.innerHTML = '';
        for (const preset of PRESET_HIGHLIGHT_COLORS) {
            const hex = preset.hex.toLowerCase();
            const isSelected = highlightColors.includes(hex);

            const btn = document.createElement('button');
            btn.type = 'button';
            btn.className = `highlight-color-swatch-btn${isSelected ? ' active' : ''}`;
            btn.style.backgroundColor = hex;
            btn.title = `${preset.name} (${hex})`;
            btn.setAttribute('aria-label', `${preset.name} ${hex}`);
            if (isSelected) {
                btn.textContent = '✓';
            }

            btn.addEventListener('click', () => handlePresetColorClick(hex));
            els.highlightPresetColors.appendChild(btn);
        }
    }

    function renderSelectedColors() {
        els.highlightSelectedColors.innerHTML = '';
        highlightColors.forEach((hex, idx) => {
            const item = document.createElement('div');
            item.className = 'highlight-selected-color-item';

            const dot = document.createElement('span');
            dot.className = 'highlight-selected-color-dot';
            dot.style.backgroundColor = hex;
            item.appendChild(dot);

            const code = document.createElement('code');
            code.textContent = hex;
            item.appendChild(code);

            const removeBtn = document.createElement('button');
            removeBtn.type = 'button';
            removeBtn.className = 'highlight-remove-color-btn';
            removeBtn.setAttribute('aria-label', `移除顏色 ${hex}`);
            removeBtn.textContent = '×';
            removeBtn.addEventListener('click', () => removeHighlightColor(idx));
            item.appendChild(removeBtn);

            els.highlightSelectedColors.appendChild(item);
        });
    }

    function renderHighlightVisualUI() {
        renderPresetSwatches();
        renderSelectedColors();
        updateHighlightPreview();
    }

    function handlePresetColorClick(hex) {
        if (highlightColors.includes(hex)) {
            if (highlightColors.length <= MIN_HIGHLIGHT_COLORS) {
                showToast('至少要保留一個顏色', true);
                return;
            }
            highlightColors = highlightColors.filter(c => c !== hex);
            saveHighlightColors(highlightColors);
            settingsDirty = true;
            renderHighlightVisualUI();
        } else {
            if (highlightColors.length >= MAX_HIGHLIGHT_COLORS) {
                showToast('最多只能選 8 個顏色', true);
                return;
            }
            highlightColors.push(hex);
            saveHighlightColors(highlightColors);
            settingsDirty = true;
            renderHighlightVisualUI();
        }
    }

    function handleAddCustomColor() {
        const raw = els.highlightCustomColorInput.value;
        const hex = normalizeHexColor(raw);
        if (!hex) {
            return;
        }
        if (highlightColors.includes(hex)) {
            showToast('這個顏色已經在清單裡', true);
            return;
        }
        if (highlightColors.length >= MAX_HIGHLIGHT_COLORS) {
            showToast('最多只能選 8 個顏色', true);
            return;
        }
        highlightColors.push(hex);
        saveHighlightColors(highlightColors);
        settingsDirty = true;
        renderHighlightVisualUI();
    }

    function removeHighlightColor(idx) {
        if (highlightColors.length <= MIN_HIGHLIGHT_COLORS) {
            showToast('至少要保留一個顏色', true);
            return;
        }
        highlightColors.splice(idx, 1);
        saveHighlightColors(highlightColors);
        settingsDirty = true;
        renderHighlightVisualUI();
    }

    function initHighlightVisualSettings() {
        const flowEnabled = loadHighlightFlow();
        els.highlightFlowToggle.checked = flowEnabled;
        highlightColors = loadHighlightColors();

        renderHighlightVisualUI();

        els.highlightFlowToggle.addEventListener('change', () => {
            const enabled = els.highlightFlowToggle.checked;
            saveHighlightFlow(enabled);
            settingsDirty = true;
            updateHighlightPreview();
        });

        els.highlightAddColorBtn.addEventListener('click', handleAddCustomColor);
    }

    // === 名稱顯示 ===

    async function loadDisplaySettings() {
        const settings = await fetchJson('api/settings/display');
        const radio = document.querySelector(`input[name="display-mode"][value="${settings.nameDisplayMode}"]`);
        if (radio) {
            radio.checked = true;
        }
        toggleAliasEditor();
    }

    async function handleDisplayModeChange() {
        const mode = document.querySelector('input[name="display-mode"]:checked').value;
        try {
            await fetchJson('api/settings/display', {
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
        const url = groupId ? `api/users?groupId=${encodeURIComponent(groupId)}` : 'api/users';
        const [users, aliases] = await Promise.all([
            fetchJson(url),
            fetchJson('api/settings/aliases')
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
                await fetchJson(`api/settings/aliases/${encodeURIComponent(userId)}`, {
                    method: 'PUT',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({ alias })
                });
            } else {
                // 清空別名等於刪除；本來就沒有別名時會回 404，那也是想要的結果，
                // 但其他錯誤碼要真的當成失敗，不能一律當成功
                const response = await fetch(`api/settings/aliases/${encodeURIComponent(userId)}`, { method: 'DELETE' });
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
        const settings = await fetchJson('api/settings/pii-masking');
        els.piiNationalIdToggle.checked = settings.maskNationalId;
        els.piiMobileToggle.checked = settings.maskMobilePhone;
        els.piiLandlineToggle.checked = settings.maskLandline;
        els.piiNhiToggle.checked = settings.maskNhiCard;
    }

    // 失敗時一定要把該開關轉回原狀。這個 PUT 送的是四個旗標的完整快照，所以「畫面狀態」與
    // 「DB 狀態」一旦分岔就會被下一次成功的存檔固化下來：使用者關掉身分證但 PUT 失敗（畫面
    // 已經是未勾、DB 仍是 true），接著關掉健保卡而這次成功，送出去的 payload 會把身分證一起
    // 關掉——等於靜默關閉一個使用者從來沒成功關過的遮蔽開關，而且畫面上看不出任何異常。
    // 這是隱私開關，寧可讓使用者看到「改不動」，也不能讓它在背後自己變動。
    async function handlePiiMaskingChange(event) {
        const toggle = event?.target ?? null;
        try {
            await fetchJson('api/settings/pii-masking', {
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
            if (toggle) {
                toggle.checked = !toggle.checked;
            }
            showToast('更新失敗，設定未變更', true);
        }
    }

    // === 訊息保留天數：不可逆操作，送出前要求二次確認（見 SettingsController 的說明）===

    async function loadRetentionSettings() {
        const settings = await fetchJson('api/settings/retention');
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
            await fetchJson('api/settings/retention', {
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

    // === 主機狀態（需求4：Web 端要能看到另外幾台服務是否正常運作，見
    //     docs/POST-CONSOLIDATION-REVIEW-PLAN.md 批次D。狀態燈由伺服器端算好，見
    //     SettingsController.ComputeStatus——這裡只負責照燈號挑對應的樣式與文字）===

    const HOST_STATUS_BADGE = {
        Online: { className: 'text-bg-success', label: '正常' },
        Delayed: { className: 'text-bg-warning', label: '遲滯' },
        Offline: { className: 'text-bg-secondary', label: '離線' }
    };

    function formatRelativeTime(iso) {
        const seconds = Math.max(0, Math.round((Date.now() - new Date(iso).getTime()) / 1000));
        if (seconds < 60) {
            return `${seconds} 秒前`;
        }
        const minutes = Math.round(seconds / 60);
        if (minutes < 60) {
            return `${minutes} 分鐘前`;
        }
        const hours = Math.round(minutes / 60);
        if (hours < 24) {
            return `${hours} 小時前`;
        }
        return `${Math.round(hours / 24)} 天前`;
    }

    function renderHostStatusBadge(status) {
        const badge = document.createElement('span');
        const meta = HOST_STATUS_BADGE[status] || HOST_STATUS_BADGE.Offline;
        badge.className = `badge ${meta.className}`;
        badge.textContent = meta.label;
        return badge;
    }

    // 這一列的心跳是怎麼進來的：Push＝那台主機自己送來（或自己直寫），
    // Pull＝本機主動輪詢 Edge 取回來的。舊資料沒有這個欄位，顯示為未知
    function renderHostChannelBadge(channel) {
        if (!channel) {
            return document.createTextNode('—');
        }
        const badge = document.createElement('span');
        const isPull = channel === 'Pull';
        badge.className = `badge ${isPull ? 'text-bg-info' : 'text-bg-secondary'}`;
        badge.textContent = isPull ? '輪詢' : '推送';
        badge.title = isPull
            ? '本機主動輪詢 Edge 取回（防火牆只開通 core→edge 時的方向）'
            : '該主機主動送來';
        return badge;
    }

    function renderHostOutboxCell(pending, oldestAgeSeconds) {
        // null＝這台主機不收 webhook（Core／Viewer），沒有 outbox 可言，不是「查不到資料」
        if (pending === null || pending === undefined) {
            return document.createTextNode('—');
        }
        if (pending === 0) {
            return document.createTextNode('0');
        }
        const minutes = oldestAgeSeconds != null ? Math.round(oldestAgeSeconds / 60) : null;
        return document.createTextNode(
            minutes !== null ? `${pending} 筆（最舊 ${minutes} 分鐘）` : `${pending} 筆`);
    }

    function renderHostHeartbeatRow(row, fingerprintMismatch) {
        const tr = document.createElement('tr');

        const roleTd = document.createElement('td');
        roleTd.textContent = row.role;
        tr.appendChild(roleTd);

        const machineTd = document.createElement('td');
        machineTd.textContent = row.machineName;
        tr.appendChild(machineTd);

        const statusTd = document.createElement('td');
        statusTd.appendChild(renderHostStatusBadge(row.status));
        tr.appendChild(statusTd);

        const lastSeenTd = document.createElement('td');
        lastSeenTd.textContent = formatRelativeTime(row.lastSeenAt);
        lastSeenTd.title = new Date(row.lastSeenAt).toLocaleString('zh-TW');
        tr.appendChild(lastSeenTd);

        const channelTd = document.createElement('td');
        channelTd.appendChild(renderHostChannelBadge(row.channel));
        tr.appendChild(channelTd);

        const outboxTd = document.createElement('td');
        outboxTd.appendChild(renderHostOutboxCell(row.outboxPending, row.outboxOldestAgeSeconds));
        tr.appendChild(outboxTd);

        const fingerprintTd = document.createElement('td');
        if (row.encryptionKeyFingerprint) {
            const code = document.createElement('code');
            code.textContent = row.encryptionKeyFingerprint;
            if (fingerprintMismatch) {
                code.className = 'badge text-bg-danger';
            }
            fingerprintTd.appendChild(code);
        } else {
            fingerprintTd.textContent = '—';
        }
        tr.appendChild(fingerprintTd);

        const actionTd = document.createElement('td');
        actionTd.className = 'text-end';
        const removeBtn = document.createElement('button');
        removeBtn.type = 'button';
        removeBtn.className = 'btn btn-outline-danger btn-sm';
        removeBtn.textContent = '移除';
        removeBtn.addEventListener('click', () => handleDeleteHostHeartbeat(row.role, row.machineName));
        actionTd.appendChild(removeBtn);
        tr.appendChild(actionTd);

        return tr;
    }

    async function handleDeleteHostHeartbeat(role, machineName) {
        // 主機更名、角色改了、或那台機器退役時才需要——不做自動清除，見 SettingsController 的說明
        const confirmed = window.confirm(
            `確定要移除「${role} / ${machineName}」這筆主機狀態紀錄嗎？如果這台主機還在運作，` +
            `下次心跳回報時會重新出現。`);
        if (!confirmed) {
            return;
        }

        try {
            await fetchJson(`api/settings/host-heartbeats/${encodeURIComponent(role)}/${encodeURIComponent(machineName)}`,
                { method: 'DELETE' });
            await Promise.all([loadDatabaseStatus(), loadHostHeartbeats(), loadMessageFlow()]);
            showToast('已移除');
        } catch {
            showToast('移除失敗', true);
        }
    }

    async function loadDatabaseStatus() {
        const status = await fetchJson('api/settings/database-status');

        // 只有本機這台主機的狀態（見 DatabaseStartupDecision 說明）——AllInOne 以外的模式
        // sqliteFallbackActive 恆為 false，這裡不用特別分模式處理
        els.databaseProviderNote.textContent =
            `本機（這台檢視端）目前使用的資料庫：${status.effectiveProvider}` +
            (status.sqliteFallbackActive ? '（救援模式）' : '');

        els.databaseFallbackWarning.classList.toggle('d-none', !status.sqliteFallbackActive);
        if (status.sqliteFallbackActive) {
            els.databaseFallbackWarning.textContent =
                `目前以 SQLite 救援模式運作——設定的 SQL Server 啟動時連線／schema 驗證失敗` +
                `（${status.sqliteFallbackReason || '原因不明'}），資料暫時寫入本機 SQLite。` +
                `修好 SQL Server 後重新啟動即可切回，這段期間的資料不會自動搬過去。`;
        }
    }

    async function loadMessageFlow() {
        const flow = await fetchJson('api/settings/message-flow');

        if (flow.lastMessageAt) {
            els.messageFlowNote.textContent = `最後收到訊息：${new Date(flow.lastMessageAt).toLocaleString('zh-TW')}`;
        } else {
            els.messageFlowNote.textContent = '最後收到訊息：尚無資料';
        }

        const isSilent = flow.status === 'Silent';
        els.messageFlowWarning.classList.toggle('d-none', !isSilent);
        if (isSilent) {
            els.messageFlowWarning.textContent =
                '已超過警示門檻未收到新訊息——可能是 webhook 鏈路中斷（請檢查 LINE Developers Console 的 Webhook URL 設定、' +
                'SSL/TLS 憑證效期、或 EdgeProxy 轉發是否正常），可對照本頁下方各主機的 Outbox 積壓數字判斷斷點。';
        }
    }

    async function loadHostHeartbeats() {
        const rows = await fetchJson('api/settings/host-heartbeats');

        els.hostHeartbeatsEmpty.classList.toggle('d-none', rows.length > 0);

        // 各主機的 Encryption:Key 沒對齊時，加密內容在那台主機上會顯示成 ENC1: 亂碼、媒體
        // 一律回 404——與其等使用者自己發現，不如在指紋不一致時直接在畫面上標出來
        const distinctFingerprints = new Set(rows.map(r => r.encryptionKeyFingerprint).filter(Boolean));
        const mismatch = distinctFingerprints.size > 1;
        els.hostHeartbeatsFingerprintWarning.classList.toggle('d-none', !mismatch);
        if (mismatch) {
            els.hostHeartbeatsFingerprintWarning.textContent =
                '偵測到不同主機的加密金鑰指紋不一致——請確認每一台直連資料庫的主機（AllInOne／Core／Viewer）' +
                'Encryption:Key 設定完全相同，否則加密內容會顯示成亂碼、媒體會一律 404。';
        }

        els.hostHeartbeatsTbody.innerHTML = '';
        for (const row of rows) {
            els.hostHeartbeatsTbody.appendChild(renderHostHeartbeatRow(row, mismatch));
        }
    }

    async function handleHostHeartbeatsRefresh() {
        try {
            await Promise.all([loadDatabaseStatus(), loadHostHeartbeats(), loadMessageFlow()]);
        } catch {
            showToast('讀取主機狀態失敗', true);
        }
    }

    // === 初始化 ===
    // 設定現在是聊天頁裡的 modal，不再是獨立頁面：元素綁定跟不需要資料的監聽器在頁面
    // 載入時就做完，實際打 API 撈資料延後到第一次打開 modal 才做（shown.bs.modal），
    // 避免聊天頁一開就多打一輪只有設定頁才用得到的請求。

    function wireElements() {
        els.fontBasePxInput = $('font-base-px-input');
        els.fullWidthToggle = $('full-width-toggle');
        els.keywordTbody = $('keyword-tbody');
        els.keywordForm = $('keyword-form');
        els.keywordInput = $('keyword-input');
        els.replacementDefault = $('replacement-default');
        els.replacementCustom = $('replacement-custom');
        els.replacementInput = $('replacement-input');
        els.scopeAll = $('scope-all');
        els.scopeSelected = $('scope-selected');
        els.scopeGroupCheckboxes = $('scope-group-checkboxes');
        els.highlightKeywordTbody = $('highlight-keyword-tbody');
        els.highlightKeywordForm = $('highlight-keyword-form');
        els.highlightKeywordInput = $('highlight-keyword-input');
        els.highlightScopeAll = $('highlight-scope-all');
        els.highlightScopeSelected = $('highlight-scope-selected');
        els.highlightScopeGroupCheckboxes = $('highlight-scope-group-checkboxes');
        els.highlightUserTbody = $('highlight-user-tbody');
        els.highlightUsersEmpty = $('highlight-users-empty');
        els.highlightFlowToggle = $('highlight-flow-toggle');
        els.highlightPresetColors = $('highlight-preset-colors');
        els.highlightCustomColorInput = $('highlight-custom-color-input');
        els.highlightAddColorBtn = $('highlight-add-color-btn');
        els.highlightSelectedColors = $('highlight-selected-colors');
        els.highlightPreviewBubble = $('highlight-preview-bubble');
        els.aliasEditor = $('alias-editor');
        els.aliasGroupFilter = $('alias-group-filter');
        els.aliasTbody = $('alias-tbody');
        els.piiNationalIdToggle = $('pii-national-id-toggle');
        els.piiMobileToggle = $('pii-mobile-toggle');
        els.piiLandlineToggle = $('pii-landline-toggle');
        els.piiNhiToggle = $('pii-nhi-toggle');
        els.retentionDaysInput = $('retention-days-input');
        els.retentionSaveBtn = $('retention-save-btn');
        els.hostHeartbeatsTbody = $('host-heartbeats-tbody');
        els.hostHeartbeatsEmpty = $('host-heartbeats-empty');
        els.hostHeartbeatsFingerprintWarning = $('host-heartbeats-fingerprint-warning');
        els.databaseFallbackWarning = $('database-fallback-warning');
        els.databaseProviderNote = $('database-provider-note');
        els.messageFlowNote = $('message-flow-note');
        els.messageFlowWarning = $('message-flow-warning');
        els.hostHeartbeatsRefreshBtn = $('host-heartbeats-refresh-btn');
        els.settingsModal = $('settings-modal');
        els.settingsModalBody = $('settings-modal-body');
    }

    function wireStaticListeners() {
        initFontBasePx();
        initFullWidthToggle();
        initHighlightVisualSettings();

        els.replacementCustom.addEventListener('change', () => { els.replacementInput.disabled = false; });
        els.replacementDefault.addEventListener('change', () => { els.replacementInput.disabled = true; });
        els.scopeSelected.addEventListener('change', () => els.scopeGroupCheckboxes.classList.remove('d-none'));
        els.scopeAll.addEventListener('change', () => els.scopeGroupCheckboxes.classList.add('d-none'));
        els.keywordForm.addEventListener('submit', handleKeywordSubmit);

        els.highlightScopeSelected.addEventListener('change', () => els.highlightScopeGroupCheckboxes.classList.remove('d-none'));
        els.highlightScopeAll.addEventListener('change', () => els.highlightScopeGroupCheckboxes.classList.add('d-none'));
        els.highlightKeywordForm.addEventListener('submit', handleHighlightKeywordSubmit);

        document.querySelectorAll('input[name="display-mode"]').forEach(
            radio => radio.addEventListener('change', handleDisplayModeChange));
        els.aliasGroupFilter.addEventListener('change', loadAliasEditor);

        for (const toggle of [els.piiNationalIdToggle, els.piiMobileToggle, els.piiLandlineToggle, els.piiNhiToggle]) {
            toggle.addEventListener('change', handlePiiMaskingChange);
        }
        els.retentionSaveBtn.addEventListener('click', handleRetentionSave);
        els.hostHeartbeatsRefreshBtn.addEventListener('click', handleHostHeartbeatsRefresh);

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
        groupsCache = await fetchJson('api/groups');
        renderScopeCheckboxes();
        renderHighlightScopeCheckboxes();

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

        await Promise.all([
            loadKeywords(), loadHighlightKeywords(), loadHighlightUsers(),
            loadDisplaySettings(), loadPiiMaskingSettings(), loadRetentionSettings(),
            loadDatabaseStatus(), loadHostHeartbeats(), loadMessageFlow()
        ]);
    }

    function init() {
        wireElements();
        wireStaticListeners();
    }

    document.addEventListener('DOMContentLoaded', init);
})();
