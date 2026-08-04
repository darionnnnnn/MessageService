(() => {
    'use strict';

    const els = {};
    let groupsCache = [];

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
            showToast('別名已更新');
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

    function applyFontBasePx(px) {
        els.settingsApp.style.setProperty('--font-base-px', `${px}px`);
    }

    function initFontBasePx() {
        const px = loadFontBasePx();
        els.fontBasePxInput.value = px;
        applyFontBasePx(px);

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

    // === 初始化 ===

    async function init() {
        els.settingsApp = $('settings-app');
        els.fontBasePxInput = $('font-base-px-input');
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

        initFontBasePx();

        els.replacementCustom.addEventListener('change', () => { els.replacementInput.disabled = false; });
        els.replacementDefault.addEventListener('change', () => { els.replacementInput.disabled = true; });
        els.scopeSelected.addEventListener('change', () => els.scopeGroupCheckboxes.classList.remove('d-none'));
        els.scopeAll.addEventListener('change', () => els.scopeGroupCheckboxes.classList.add('d-none'));
        els.keywordForm.addEventListener('submit', handleKeywordSubmit);

        document.querySelectorAll('input[name="display-mode"]').forEach(
            radio => radio.addEventListener('change', handleDisplayModeChange));
        els.aliasGroupFilter.addEventListener('change', loadAliasEditor);

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

        await Promise.all([loadKeywords(), loadDisplaySettings()]);
    }

    document.addEventListener('DOMContentLoaded', init);
})();
