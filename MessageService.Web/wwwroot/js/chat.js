(() => {
    'use strict';

    const POLL_INTERVAL_MS = 3000;
    // 側欄（新群組/預覽/排序）不需要跟訊息輪詢一樣即時，且 /api/groups 一次要跑好幾個查詢，
    // 沒必要用同一個頻率打
    const GROUP_POLL_INTERVAL_MS = 10000;
    const NEAR_BOTTOM_THRESHOLD_PX = 80;
    const NEAR_TOP_THRESHOLD_PX = 8;
    const INITIAL_DAYS = 3;
    const LOAD_MORE_DAYS = 7;
    // 與 MessagesController.MaxDays 對齊；超過就別再放大視窗，免得按鈕變成按了沒反應
    const MAX_DAYS_WINDOW = 3650;
    // 與後端 MessagesController.MaxStatusIds 一致；超過的話整串 ?ids= 會撞上 IIS 預設的
    // maxQueryString（2048），而且失敗後那些內容會永遠卡在載入中，因為狀態輪詢再也回不來
    const STATUS_POLL_BATCH_SIZE = 100;
    // 與後端 MessagesController.MessageWindowLimit 對齊
    const MESSAGE_WINDOW_LIMIT = 500;
    // 與後端 MessagesController.SearchCandidateLimit 對齊
    const SEARCH_CANDIDATE_LIMIT = 300;
    const AVATAR_COLORS = ['#f28b82', '#fbbc04', '#34a853', '#4285f4', '#a142f4', '#ff6d01', '#00acc1', '#c2185b'];
    const GROUP_AVATAR_COLOR = '#9AACC2';
    const FONT_SIZE_STORAGE_KEY = 'chat-font-size';
    const FONT_SIZES = ['small', 'medium', 'large'];
    const DEFAULT_FONT_SIZE = 'medium';
    const HIGHLIGHT_FLOW_STORAGE_KEY = 'chat-highlight-flow';
    const HIGHLIGHT_COLORS_STORAGE_KEY = 'chat-highlight-colors';
    const HIGHLIGHT_OPACITY_STORAGE_KEY = 'chat-highlight-opacity';
    const DEFAULT_HIGHLIGHT_OPACITY = 0.5;
    // 顏色數量上限只有這一份定義，設定頁透過共享出口取用
    const MAX_HIGHLIGHT_COLORS = 8;
    const DEFAULT_HIGHLIGHT_COLORS = ['#06c755', '#ffc53d', '#ff6b57', '#a66cff'];
    const URL_REGEX = /(https?:\/\/[^\s]+)/g;

    // 對應後端 AvatarIconCatalog 的 IconKey；同一份代號清單，兩邊各自維護一份對照表
    const ICON_EMOJI = {
        bear: '🐻', cat: '🐱', rabbit: '🐰', bird: '🐦', deer: '🦌', penguin: '🐧',
        dolphin: '🐬', owl: '🦉', koala: '🐨', panda: '🐼', sheep: '🐑', otter: '🦦',
        hedgehog: '🦔', seal: '🦭', swan: '🦢', whale: '🐳', flower: '🌼',
        'cherry-blossom': '🌸', 'maple-leaf': '🍁', sunflower: '🌻', tulip: '🌷',
        clover: '🍀', 'ginkgo-leaf': '🍂', lotus: '🪷',
        group: '👥'
    };
    const UNKNOWN_AVATAR_EMOJI = '❔';

    const state = {
        groups: [],
        groupId: null,
        oldestId: null,
        newestId: null,
        // 歷史檢視時「目前視窗內最新一則」的 id。不能重用 newestId——那個在歷史檢視下的語意是
        // 「即時輪詢的基準」，兩者在追上最新之前是不同的值
        windowNewestId: null,
        daysWindow: INITIAL_DAYS,
        hasMoreOlder: false,
        loadingOlder: false,
        loadingNewer: false,
        polling: false,
        groupsPolling: false,
        // 每次切換群組就 +1；非同步請求回來時若對不上，代表是前一個群組的過期回應，必須丟棄
        requestToken: 0,
        following: true,
        unreadCount: 0,
        // 側欄各群組的「最後已讀訊息 Id」基準，每台裝置各自記在 localStorage（見 READ_STATE_KEY）。
        // 未讀數字本身由後端依這份基準計算，前端只負責維護基準
        readState: {},
        // 側欄收合狀態（expanded / rail / hidden）與展開時的寬度（px），皆存 localStorage
        sidebarState: 'expanded',
        sidebarWidth: 320,
        fullscreen: false,
        savedSidebarStateBeforeFullscreen: null,
        pendingContentIds: new Set(),
        lastAppendedDateKey: null,
        lastAppendedSenderId: null,
        connectionOk: true,
        // 從搜尋結果跳轉到歷史上下文時為 true：pollNewer 暫停把新訊息接到視窗尾端
        // （避免時間軸斷層），使用者要點「回到最新」整個重置回即時畫面才會恢復
        historicalView: false,
        noMoreNewer: false,
        // latch 生效當下該群組的 lastMessageId，用來判斷「是否有新訊息進來」
        noMoreNewerAt: null,
        searchScope: 'group',
        // 跟 requestToken 分開算——切群組不該讓正在飛的搜尋請求作廢，反之亦然
        searchRequestToken: 0,
        initialEmpty: false,
        highlightRules: { keywords: [], users: [] },
        // 高亮重掃時要對回訊息資料，但只需要這兩個欄位——存整包訊息物件會讓長時間停在
        // 同一個群組的工作階段一直長大（輪詢每 3 秒往裡面塞）
        messagesCache: new Map(),
        groupsRefreshQueued: false
    };

    const els = {};

    function $(id) {
        return document.getElementById(id);
    }

    function showToast(message, isError) {
        const container = $('toast-container');
        if (!container) {
            return;
        }
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

        container.appendChild(toast);
        const instance = bootstrap.Toast.getOrCreateInstance(toast, { delay: 2500 });
        instance.show();
        toast.addEventListener('hidden.bs.toast', () => toast.remove());
    }

    window.messageServiceToast = showToast;

    // === 訊息高亮色彩處理與共享出口 ===

    function normalizeHexColor(color) {
        if (!color || typeof color !== 'string') {
            return null;
        }
        const trimmed = color.trim().toLowerCase();
        return /^#[0-9a-f]{6}$/.test(trimmed) ? trimmed : null;
    }

    // 把 #rrggbb 轉成帶透明度的 rgba()，給發光陰影用——邊框是漸層，
    // 光暈取第一個顏色就好，不然多色光暈疊在一起會糊成一團灰
    function hexToGlow(hex, alpha) {
        const value = normalizeHexColor(hex);
        if (!value) {
            return `rgba(6, 199, 85, ${alpha})`;
        }
        const r = parseInt(value.slice(1, 3), 16);
        const g = parseInt(value.slice(3, 5), 16);
        const b = parseInt(value.slice(5, 7), 16);
        return `rgba(${r}, ${g}, ${b}, ${alpha})`;
    }

    // 光暈透明度統一以 opacity * 0.7 計算，聊天頁與設定頁預覽共用
    function computeHighlightGlow(hex, opacity) {
        return hexToGlow(hex, opacity * 0.7);
    }

    function buildHighlightGradient(colors, opacity = DEFAULT_HIGHLIGHT_OPACITY) {
        const list = (colors && colors.length > 0) ? colors : DEFAULT_HIGHLIGHT_COLORS;
        const rgbaColors = list.map(c => hexToGlow(c, opacity));
        if (rgbaColors.length === 1) {
            return `linear-gradient(135deg, ${rgbaColors[0]}, ${rgbaColors[0]})`;
        }
        return `linear-gradient(135deg, ${rgbaColors.join(', ')})`;
    }

    function loadHighlightFlow() {
        try {
            const saved = localStorage.getItem(HIGHLIGHT_FLOW_STORAGE_KEY);
            return saved === null ? true : saved === '1';
        } catch {
            return true;
        }
    }

    function loadHighlightColors() {
        try {
            const saved = localStorage.getItem(HIGHLIGHT_COLORS_STORAGE_KEY);
            if (saved) {
                const parsed = JSON.parse(saved);
                if (Array.isArray(parsed)) {
                    const normalized = parsed.map(normalizeHexColor).filter(Boolean);
                    if (normalized.length > 0) {
                        return normalized.slice(0, MAX_HIGHLIGHT_COLORS);
                    }
                }
            }
        } catch {
        }
        return [...DEFAULT_HIGHLIGHT_COLORS];
    }

    function loadHighlightOpacity() {
        try {
            const saved = localStorage.getItem(HIGHLIGHT_OPACITY_STORAGE_KEY);
            if (saved !== null) {
                const parsed = parseFloat(saved);
                if (!Number.isNaN(parsed) && parsed >= 0.1 && parsed <= 1.0) {
                    return parsed;
                }
            }
        } catch {
        }
        return DEFAULT_HIGHLIGHT_OPACITY;
    }

    function applyHighlightVisualSettings() {
        const colors = loadHighlightColors();
        const flowEnabled = loadHighlightFlow();
        const opacity = loadHighlightOpacity();
        const gradient = buildHighlightGradient(colors, opacity);
        const glow = computeHighlightGlow(colors[0], opacity);

        document.documentElement.style.setProperty('--highlight-gradient', gradient);
        document.documentElement.style.setProperty('--highlight-glow', glow);
        document.documentElement.classList.toggle('highlight-flowing', flowEnabled);
    }

    window.messageServiceHighlight = {
        buildHighlightGradient,
        hexToGlow,
        computeHighlightGlow,
        normalizeHexColor,
        loadHighlightFlow,
        loadHighlightColors,
        loadHighlightOpacity,
        applyHighlightVisualSettings,
        HIGHLIGHT_FLOW_STORAGE_KEY,
        HIGHLIGHT_COLORS_STORAGE_KEY,
        HIGHLIGHT_OPACITY_STORAGE_KEY,
        DEFAULT_HIGHLIGHT_COLORS,
        DEFAULT_HIGHLIGHT_OPACITY,
        MAX_HIGHLIGHT_COLORS
    };

    // === 通用快捷選單（Context Menu）===

    let activeContextMenu = null;

    function closeContextMenu() {
        if (!activeContextMenu) {
            return;
        }
        activeContextMenu.cleanup();
        activeContextMenu.element.remove();
        activeContextMenu = null;
    }

    function showContextMenu(anchorEvent, items) {
        closeContextMenu();
        if (!items || items.length === 0) {
            return;
        }

        const menu = document.createElement('div');
        menu.className = 'context-menu';
        menu.setAttribute('role', 'menu');
        menu.tabIndex = -1;

        for (const item of items) {
            const btn = document.createElement('button');
            btn.type = 'button';
            btn.className = 'context-menu-item' + (item.danger ? ' context-menu-item-danger' : '');
            btn.setAttribute('role', 'menuitem');
            btn.textContent = item.label;
            btn.addEventListener('click', (e) => {
                e.stopPropagation();
                closeContextMenu();
                if (typeof item.onSelect === 'function') {
                    item.onSelect();
                }
            });
            menu.appendChild(btn);
        }

        menu.style.visibility = 'hidden';
        menu.style.left = '0px';
        menu.style.top = '0px';
        document.body.appendChild(menu);

        const rect = menu.getBoundingClientRect();
        const menuWidth = rect.width;
        const menuHeight = rect.height;
        const viewportWidth = window.innerWidth;
        const viewportHeight = window.innerHeight;

        const clickX = anchorEvent.clientX ?? 0;
        const clickY = anchorEvent.clientY ?? 0;

        let posX = clickX;
        let posY = clickY;

        if (posX + menuWidth > viewportWidth) {
            posX = Math.max(0, clickX - menuWidth);
        }
        if (posY + menuHeight > viewportHeight) {
            posY = Math.max(0, clickY - menuHeight);
        }

        menu.style.left = `${posX}px`;
        menu.style.top = `${posY}px`;
        menu.style.visibility = '';

        // 鍵盤導航：↑↓ 在項目間移動，Enter 觸發，Esc 關閉
        const handleKeyDown = (e) => {
            const buttons = Array.from(menu.querySelectorAll('.context-menu-item:not(:disabled)'));
            if (buttons.length === 0) {
                return;
            }
            const currentIndex = buttons.indexOf(document.activeElement);
            if (e.key === 'ArrowDown') {
                e.preventDefault();
                const nextIndex = (currentIndex + 1) % buttons.length;
                buttons[nextIndex]?.focus();
            } else if (e.key === 'ArrowUp') {
                e.preventDefault();
                const prevIndex = (currentIndex - 1 + buttons.length) % buttons.length;
                buttons[prevIndex]?.focus();
            } else if (e.key === 'Escape') {
                e.preventDefault();
                closeContextMenu();
            }
        };

        const handlePointerDownOutside = (e) => {
            if (!menu.contains(e.target)) {
                closeContextMenu();
            }
        };

        const handleScrollOrResize = () => {
            closeContextMenu();
        };

        menu.addEventListener('keydown', handleKeyDown);
        window.addEventListener('scroll', handleScrollOrResize, { capture: true, passive: true });
        window.addEventListener('resize', handleScrollOrResize, { passive: true });

        const timer = setTimeout(() => {
            document.addEventListener('pointerdown', handlePointerDownOutside, true);
        }, 0);

        const cleanup = () => {
            clearTimeout(timer);
            menu.removeEventListener('keydown', handleKeyDown);
            document.removeEventListener('pointerdown', handlePointerDownOutside, true);
            window.removeEventListener('scroll', handleScrollOrResize, { capture: true });
            window.removeEventListener('resize', handleScrollOrResize);
        };

        activeContextMenu = { element: menu, cleanup };

        // 自動聚焦第一項
        const firstBtn = menu.querySelector('.context-menu-item');
        if (firstBtn) {
            firstBtn.focus();
        }
    }

    function showGroupContextMenu(anchorEvent, group) {
        const items = [
            {
                label: '刪除歷史訊息',
                danger: false,
                onSelect: () => openDeleteModal('messages', group)
            },
            {
                label: '刪除群組',
                danger: true,
                onSelect: () => openDeleteModal('group', group)
            }
        ];
        showContextMenu(anchorEvent, items);
    }

    // === 訊息高亮規則與選單 ===

    async function loadHighlightRules() {
        try {
            const rules = await fetchJson('api/settings/highlight-rules');
            state.highlightRules = {
                keywords: Array.isArray(rules?.keywords) ? rules.keywords : [],
                users: Array.isArray(rules?.users) ? rules.users : []
            };
        } catch (err) {
            state.highlightRules = { keywords: [], users: [] };
            console.warn('載入訊息高亮規則失敗：', err);
        }
    }

    // 取得訊息命中高亮規則的所有關鍵字（去重、依長度由長到短排序，避免短關鍵字先切斷長關鍵字）
    function matchedHighlightKeywords(message) {
        if (!message || typeof message.text !== 'string' || message.text.length === 0) {
            return [];
        }
        const keywords = state.highlightRules?.keywords;
        if (!Array.isArray(keywords) || keywords.length === 0) {
            return [];
        }

        const textLower = message.text.toLowerCase();
        const currentGroupId = state.groupId;
        const matched = new Set();

        for (const kwRule of keywords) {
            if (!kwRule || !kwRule.keyword) {
                continue;
            }
            const matchScope = kwRule.applyToAllGroups === true ||
                (Array.isArray(kwRule.groupIds) && currentGroupId && kwRule.groupIds.includes(currentGroupId));
            if (matchScope && textLower.includes(kwRule.keyword.toLowerCase())) {
                matched.add(kwRule.keyword);
            }
        }

        return Array.from(matched).sort((a, b) => b.length - a.length);
    }

    function isHighlighted(message) {
        if (!message) {
            return false;
        }
        if (matchedHighlightKeywords(message).length > 0) {
            return true;
        }

        // 人員命中判定：message.userId 非空時比對
        const users = state.highlightRules?.users;
        if (Array.isArray(users) && users.length > 0 && message.userId) {
            const currentGroupId = state.groupId;
            for (const userRule of users) {
                if (userRule && userRule.userId === message.userId) {
                    if (userRule.groupId === null || userRule.groupId === currentGroupId) {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    function refreshHighlightClasses() {
        if (!els.messageList) {
            return;
        }
        const rows = els.messageList.querySelectorAll('.message-row');
        for (const row of rows) {
            const id = Number(row.dataset.messageId);
            const message = state.messagesCache.get(id);
            if (!message) {
                continue;
            }
            const bubble = row.querySelector('.bubble');
            if (bubble) {
                bubble.classList.toggle('highlighted', isHighlighted(message));
            }
        }
    }

    function showAvatarContextMenu(anchorEvent, message) {
        if (!message || !message.userId) {
            return;
        }

        const userRules = state.highlightRules?.users || [];
        const allGroupRule = userRules.find(r => r.userId === message.userId && r.groupId === null);
        const currentGroupRule = state.groupId
            ? userRules.find(r => r.userId === message.userId && r.groupId === state.groupId)
            : null;

        const items = [];

        // 全部群組項目
        if (allGroupRule) {
            items.push({
                label: '取消高亮（全部群組）',
                danger: false,
                onSelect: () => removeHighlightUser(allGroupRule.id, 'all')
            });
        } else {
            items.push({
                label: '高亮此人（全部群組）',
                danger: false,
                onSelect: () => addHighlightUser(message.userId, null)
            });
        }

        // 目前群組項目（有目前群組時才顯示）
        if (state.groupId) {
            if (currentGroupRule) {
                items.push({
                    label: '取消高亮（目前群組）',
                    danger: false,
                    onSelect: () => removeHighlightUser(currentGroupRule.id, 'current')
                });
            } else {
                items.push({
                    label: '高亮此人（目前群組）',
                    danger: false,
                    onSelect: () => addHighlightUser(message.userId, state.groupId)
                });
            }
        }

        showContextMenu(anchorEvent, items);
    }

    async function addHighlightUser(userId, groupId) {
        try {
            const response = await fetch('api/settings/highlight-users', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ userId, groupId })
            });
            if (!response.ok) {
                showToast('操作失敗，請稍後再試', true);
                return;
            }
            const created = await response.json();
            if (!state.highlightRules) {
                state.highlightRules = { keywords: [], users: [] };
            }
            if (!Array.isArray(state.highlightRules.users)) {
                state.highlightRules.users = [];
            }
            const existsIndex = state.highlightRules.users.findIndex(u => u.id === created.id);
            if (existsIndex >= 0) {
                state.highlightRules.users[existsIndex] = created;
            } else {
                state.highlightRules.users.push(created);
            }

            refreshHighlightClasses();
            showToast(groupId === null ? '已將此人加入高亮（全部群組）' : '已將此人加入高亮（目前群組）');
        } catch {
            showToast('操作失敗，請稍後再試', true);
        }
    }

    async function removeHighlightUser(id, scope) {
        try {
            const response = await fetch(`api/settings/highlight-users/${encodeURIComponent(id)}`, {
                method: 'DELETE'
            });
            if (!response.ok && response.status !== 404) {
                showToast('操作失敗，請稍後再試', true);
                return;
            }
            if (state.highlightRules?.users) {
                state.highlightRules.users = state.highlightRules.users.filter(u => u.id !== id);
            }

            refreshHighlightClasses();
            showToast(scope === 'all' ? '已取消此人的高亮（全部群組）' : '已取消此人的高亮（目前群組）');
        } catch {
            showToast('操作失敗，請稍後再試', true);
        }
    }

    // 右鍵與長按的共用觸發器：側欄群組項目與訊息頭貼共用同一套手勢
    // （桌面右鍵、手機長按 500ms、位移超過 10px 取消、長按後抑制那一次 click）。
    // openMenu 收到的參數只需要有 clientX／clientY，長按時會自己補上按下的座標。
    function attachContextMenuTriggers(el, openMenu, shouldIgnore) {
        let longPressTimer = null;
        let suppressNextClick = false;
        let startX = 0;
        let startY = 0;

        const cancelLongPress = () => {
            if (longPressTimer) {
                clearTimeout(longPressTimer);
                longPressTimer = null;
            }
        };

        el.addEventListener('contextmenu', (e) => {
            if (typeof shouldIgnore === 'function' && shouldIgnore(e)) {
                return;
            }
            e.preventDefault();
            e.stopPropagation();
            cancelLongPress();
            openMenu(e);
        });

        el.addEventListener('pointerdown', (e) => {
            if (e.button !== undefined && e.button !== 0) {
                return;
            }
            if (typeof shouldIgnore === 'function' && shouldIgnore(e)) {
                return;
            }
            e.stopPropagation();
            cancelLongPress();
            suppressNextClick = false;
            startX = e.clientX;
            startY = e.clientY;
            longPressTimer = setTimeout(() => {
                longPressTimer = null;
                suppressNextClick = true;
                openMenu({ clientX: startX, clientY: startY, target: e.target });
            }, 500);
        });

        el.addEventListener('pointermove', (e) => {
            if (!longPressTimer) {
                return;
            }
            if (Math.hypot(e.clientX - startX, e.clientY - startY) > 10) {
                cancelLongPress();
            }
        });

        el.addEventListener('pointerup', cancelLongPress);
        el.addEventListener('pointercancel', cancelLongPress);

        // 回傳「這一次 click 是不是長按帶出來的」，讓呼叫端決定要不要吞掉自己的 click 行為
        return function consumeSuppressedClick() {
            if (!suppressNextClick) {
                return false;
            }
            suppressNextClick = false;
            return true;
        };
    }

    // === 頁面內全螢幕模式 ===

    function isExcludedContextTarget(e) {
        const target = e?.target;
        if (!target || typeof target.closest !== 'function') {
            return false;
        }
        return Boolean(target.closest('.bubble, .avatar, .group-item, button, input, a, textarea, select'));
    }

    function showPanelContextMenu(anchorEvent) {
        if (isExcludedContextTarget(anchorEvent)) {
            return;
        }
        const items = [
            {
                label: state.fullscreen ? '關閉全螢幕' : '全螢幕',
                danger: false,
                onSelect: toggleFullscreen
            }
        ];
        showContextMenu(anchorEvent, items);
    }

    function enterFullscreen() {
        if (state.fullscreen) {
            return;
        }
        state.savedSidebarStateBeforeFullscreen = state.sidebarState;
        state.fullscreen = true;
        els.chatApp.classList.add('fullscreen');
        els.chatApp.classList.add('mobile-chat-open');
        renderFullscreenGroupBar();
        const activeItem = els.fullscreenGroupBar?.querySelector('.fullscreen-group-item.active');
        if (activeItem) {
            activeItem.scrollIntoView({ block: 'nearest', inline: 'nearest', behavior: 'auto' });
        }
    }

    function exitFullscreen() {
        if (!state.fullscreen) {
            return;
        }
        state.fullscreen = false;
        els.chatApp.classList.remove('fullscreen');
        if (state.savedSidebarStateBeforeFullscreen) {
            applySidebarState(state.savedSidebarStateBeforeFullscreen);
            state.savedSidebarStateBeforeFullscreen = null;
        }
    }

    function toggleFullscreen() {
        if (state.fullscreen) {
            exitFullscreen();
        } else {
            enterFullscreen();
        }
    }

    // === 群組刪除／訊息清除對話框與收斂 ===

    function openDeleteModal(type, group) {
        const isMessagesOnly = type === 'messages';

        els.groupDeleteModalTitle.textContent = isMessagesOnly ? '刪除歷史訊息' : '刪除群組';
        els.groupDeleteConfirmBtn.textContent = isMessagesOnly ? '刪除歷史訊息' : '刪除群組';
        els.groupDeleteConfirmBtn.disabled = false;
        els.groupDeleteCancelBtn.disabled = false;
        els.groupDeleteModalCloseBtn.disabled = false;

        if (isMessagesOnly) {
            els.groupDeleteModalP1.textContent = `即將刪除「${group.displayName}」的全部歷史訊息，包含所有圖片、影片、語音與檔案。`;
            els.groupDeleteModalP2.textContent = '刪除後無法復原。群組的名稱、成員與設定會保留，但因為沒有訊息了，'
                + '群組會從左側清單消失，直到有新訊息才會重新出現。';
            els.groupDeleteModalP3.classList.add('d-none');
        } else {
            els.groupDeleteModalP1.textContent = `即將刪除「${group.displayName}」，包含該群組的全部歷史訊息、圖片、影片、語音、檔案、成員快取與匿名代號。`;
            els.groupDeleteModalP2.textContent = '刪除後無法復原。';
            els.groupDeleteModalP3.textContent = 'bot 仍在這個 LINE 群組時，之後的新訊息會讓群組重新出現。要永久停止收錄，請將 bot 退出該群組。';
            els.groupDeleteModalP3.classList.remove('d-none');
        }

        els.groupDeleteConfirmBtn.onclick = () => executeGroupDelete(type, group);

        // 不可逆操作：點背景與 Esc 都不關閉，只能按「取消」或「確認」離開，
        // 執行期間兩個按鈕都會被停用，避免中途關掉對話框卻不知道刪除有沒有完成
        const modalInstance = bootstrap.Modal.getOrCreateInstance(els.groupDeleteModal, {
            backdrop: 'static',
            keyboard: false
        });
        modalInstance.show();
    }

    // Bootstrap 在 modal 的淡入轉場還沒結束時會直接忽略 hide()（內部的 _isTransitioning 旗標）。
    // 刪除很快回來時（本機資料庫常常 50ms 內就完成）關閉指令就這樣被吞掉，
    // 對話框會永遠停在「處理中…」，但刪除其實已經做完了。轉場結束後補關一次。
    function hideDeleteModal() {
        const instance = bootstrap.Modal.getInstance(els.groupDeleteModal);
        if (!instance) {
            return;
        }

        // 先掛好「淡入結束就再關一次」的守衛再呼叫 hide()，並在真的關掉時把它拆掉。
        // 不用「hide() 之後還有沒有 show class」來判斷——Bootstrap 的 hide() 有多條提早
        // return 的路徑，只有轉場中那條是我們要補的；靠 class 猜會在其他路徑留下一個
        // 永不觸發的監聽器，等下一次開啟對話框時才引爆（淡入完成的瞬間自己關掉）
        const hideAfterShown = () => instance.hide();
        els.groupDeleteModal.addEventListener('shown.bs.modal', hideAfterShown, { once: true });
        els.groupDeleteModal.addEventListener('hidden.bs.modal', () => {
            els.groupDeleteModal.removeEventListener('shown.bs.modal', hideAfterShown);
        }, { once: true });

        instance.hide();
    }

    async function executeGroupDelete(type, group) {
        els.groupDeleteConfirmBtn.disabled = true;
        els.groupDeleteConfirmBtn.textContent = '處理中…';
        els.groupDeleteCancelBtn.disabled = true;
        els.groupDeleteModalCloseBtn.disabled = true;

        const url = type === 'messages'
            ? `api/groups/${encodeURIComponent(group.groupId)}/messages`
            : `api/groups/${encodeURIComponent(group.groupId)}`;

        try {
            const response = await fetch(url, { method: 'DELETE' });

            if (response.status === 404) {
                hideDeleteModal();
                showToast('群組已不存在，清單已更新', true);
                if (state.groupId === group.groupId) {
                    resetChatPanel();
                }
                await refreshGroupList();
            } else if (!response.ok) {
                hideDeleteModal();
                showToast('刪除失敗，請稍後再試', true);
            } else {
                const result = await response.json();
                hideDeleteModal();
                const count = result?.messageCount ?? 0;
                if (type === 'messages') {
                    showToast(`已刪除「${group.displayName}」的 ${count} 則歷史訊息`);
                } else {
                    showToast(`已刪除群組「${group.displayName}」（${count} 則訊息）`);
                }
                if (state.groupId === group.groupId) {
                    resetChatPanel();
                }
                await refreshGroupList();
            }
        } catch {
            hideDeleteModal();
            showToast('刪除失敗，請稍後再試', true);
        } finally {
            els.groupDeleteConfirmBtn.disabled = false;
            els.groupDeleteConfirmBtn.textContent = type === 'messages' ? '刪除歷史訊息' : '刪除群組';
            els.groupDeleteCancelBtn.disabled = false;
            els.groupDeleteModalCloseBtn.disabled = false;
        }
    }

    function resetChatPanel() {
        state.groupId = null;
        state.oldestId = null;
        state.newestId = null;
        state.windowNewestId = null;
        state.daysWindow = INITIAL_DAYS;
        state.hasMoreOlder = false;
        state.historicalView = false;
        state.noMoreNewer = false;
        state.noMoreNewerAt = null;
        state.requestToken++;

        updateHistoricalBanner();
        updateLoadNewerButton();
        setFollowing(true);
        clearMessageList();
        updateActiveGroupItem();
        updateChatHeader(null);
        updateLoadMoreButton();
        els.chatApp.classList.remove('mobile-chat-open');
    }

    async function fetchJson(url, options) {
        const response = await fetch(url, options);
        if (!response.ok) {
            throw new Error(`HTTP ${response.status} for ${url}`);
        }
        return response.json();
    }

    function setConnectionOk(ok) {
        if (ok === state.connectionOk) {
            return;
        }
        state.connectionOk = ok;
        els.connectionBanner.classList.toggle('d-none', ok);
        els.composerStatusDot.classList.toggle('offline', !ok);
        els.composerStatusText.textContent = ok ? '唯讀檢視模式・同步中' : '唯讀檢視模式・連線中斷';
    }

    function avatarColorFor(key) {
        let hash = 0;
        for (let i = 0; i < key.length; i++) {
            hash = (hash * 31 + key.charCodeAt(i)) >>> 0;
        }
        return AVATAR_COLORS[hash % AVATAR_COLORS.length];
    }

    // === 頭貼 / 代號圖示 ===
    // 有真實 pictureUrl 就優先顯示，並在載入失敗時 fallback 換成代號圖示；
    // 沒有 pictureUrl（遮蔽/匿名模式，或群組沒有快取到照片）就直接顯示圖示
    function buildAvatarElement(extraClassName, { pictureUrl, iconKey, colorSeed, isGroup }) {
        const el = document.createElement('div');
        el.className = 'avatar' + (extraClassName ? ` ${extraClassName}` : '');

        const applyIcon = () => {
            el.classList.add('avatar-icon');
            el.style.background = isGroup ? GROUP_AVATAR_COLOR : avatarColorFor(colorSeed || '?');
            el.textContent = ICON_EMOJI[iconKey] || (isGroup ? ICON_EMOJI.group : UNKNOWN_AVATAR_EMOJI);
        };

        if (pictureUrl) {
            const img = document.createElement('img');
            img.src = pictureUrl;
            img.alt = '';
            img.referrerPolicy = 'no-referrer';
            img.addEventListener('error', () => {
                el.innerHTML = '';
                applyIcon();
            }, { once: true });
            el.appendChild(img);
        } else {
            applyIcon();
        }

        return el;
    }

    function dateKey(iso) {
        const d = new Date(iso);
        return `${d.getFullYear()}-${d.getMonth()}-${d.getDate()}`;
    }

    function formatDateSeparator(iso) {
        const d = new Date(iso);
        const now = new Date();
        const today = new Date(now.getFullYear(), now.getMonth(), now.getDate());
        const that = new Date(d.getFullYear(), d.getMonth(), d.getDate());
        const diffDays = Math.round((today - that) / 86400000);
        if (diffDays === 0) return '今天';
        if (diffDays === 1) return '昨天';
        const weekday = ['日', '一', '二', '三', '四', '五', '六'][d.getDay()];
        return `${d.getMonth() + 1}/${d.getDate()}（${weekday}）`;
    }

    function formatTime(iso) {
        return new Date(iso).toLocaleTimeString('zh-TW', { hour: '2-digit', minute: '2-digit', hour12: false });
    }

    // 側欄的最後訊息時間仿 LINE：今天顯示時刻、昨天顯示「昨天」、更早顯示月/日
    function formatListTime(iso) {
        const d = new Date(iso);
        const now = new Date();
        const today = new Date(now.getFullYear(), now.getMonth(), now.getDate());
        const that = new Date(d.getFullYear(), d.getMonth(), d.getDate());
        const diffDays = Math.round((today - that) / 86400000);
        if (diffDays === 0) return formatTime(iso);
        if (diffDays === 1) return '昨天';
        return `${d.getMonth() + 1}/${d.getDate()}`;
    }

    function createDateSeparator(iso) {
        const sep = document.createElement('div');
        sep.className = 'date-separator';
        sep.dataset.dateKey = dateKey(iso);
        const span = document.createElement('span');
        span.textContent = formatDateSeparator(iso);
        sep.appendChild(span);
        return sep;
    }

    function createTruncatedNotice() {
        const notice = document.createElement('div');
        notice.className = 'truncated-notice';
        const span = document.createElement('span');
        span.textContent = `此區間訊息過多，先顯示最近 ${MESSAGE_WINDOW_LIMIT} 則，可繼續往前載入`;
        notice.appendChild(span);
        return notice;
    }

    // === 訊息內容渲染 ===

    // 純文字訊息把網址轉成可點連結，其餘一律當純文字節點，不解析 HTML（訊息內容是外部輸入）
    function appendLinkifiedText(container, text) {
        let lastIndex = 0;
        for (const match of text.matchAll(URL_REGEX)) {
            const url = match[0];
            if (match.index > lastIndex) {
                container.appendChild(document.createTextNode(text.slice(lastIndex, match.index)));
            }
            const link = document.createElement('a');
            link.href = url;
            link.textContent = url;
            link.target = '_blank';
            link.rel = 'noopener noreferrer';
            container.appendChild(link);
            lastIndex = match.index + url.length;
        }
        if (lastIndex < text.length) {
            container.appendChild(document.createTextNode(text.slice(lastIndex)));
        }
    }

    // 把 text 裡命中的關鍵字片段包成 <span class="highlight-keyword">，其餘為純文字節點。
    // 關鍵字已依長度由長到短排序，以先命中者為準、不巢狀包裹
    function buildKeywordHighlightedFragment(text, keywords) {
        const fragment = document.createDocumentFragment();
        if (!text || !keywords || keywords.length === 0) {
            fragment.appendChild(document.createTextNode(text));
            return fragment;
        }

        const lowerText = text.toLowerCase();
        const intervals = [];

        for (const kw of keywords) {
            if (!kw) {
                continue;
            }
            const lowerKw = kw.toLowerCase();
            if (!lowerKw) {
                continue;
            }
            let cursor = 0;
            while (cursor < lowerText.length) {
                const idx = lowerText.indexOf(lowerKw, cursor);
                if (idx === -1) {
                    break;
                }
                const end = idx + lowerKw.length;
                const overlaps = intervals.some(iv => idx < iv.end && end > iv.start);
                if (!overlaps) {
                    intervals.push({ start: idx, end });
                }
                cursor = idx + 1;
            }
        }

        if (intervals.length === 0) {
            fragment.appendChild(document.createTextNode(text));
            return fragment;
        }

        intervals.sort((a, b) => a.start - b.start);

        let lastIndex = 0;
        for (const { start, end } of intervals) {
            if (start > lastIndex) {
                fragment.appendChild(document.createTextNode(text.slice(lastIndex, start)));
            }
            const span = document.createElement('span');
            span.className = 'highlight-keyword';
            span.textContent = text.slice(start, end);
            fragment.appendChild(span);
            lastIndex = end;
        }
        if (lastIndex < text.length) {
            fragment.appendChild(document.createTextNode(text.slice(lastIndex)));
        }

        return fragment;
    }

    // 對容器內不在 <a> 內部的文字節點進行關鍵字加粗放大標記
    function applyHighlightKeywordsToContainer(container, keywords) {
        if (!container || !keywords || keywords.length === 0) {
            return;
        }
        const walker = document.createTreeWalker(container, NodeFilter.SHOW_TEXT);
        const textNodes = [];
        let node = walker.nextNode();
        while (node) {
            if (!node.parentElement?.closest('a')) {
                textNodes.push(node);
            }
            node = walker.nextNode();
        }
        for (const textNode of textNodes) {
            const text = textNode.textContent;
            if (!text) {
                continue;
            }
            const lowerText = text.toLowerCase();
            const hasAnyMatch = keywords.some(kw => kw && lowerText.includes(kw.toLowerCase()));
            if (!hasAnyMatch) {
                continue;
            }
            textNode.replaceWith(buildKeywordHighlightedFragment(text, keywords));
        }
    }

    // 把 text 裡所有（不分大小寫）符合 query 的片段包成 <mark>，其餘仍是純文字節點；
    // 搜尋結果列表跟訊息串裡的關鍵字高亮共用同一份邏輯
    function buildHighlightedFragment(text, query) {
        const fragment = document.createDocumentFragment();
        if (!query) {
            fragment.appendChild(document.createTextNode(text));
            return fragment;
        }

        const lowerText = text.toLowerCase();
        const lowerQuery = query.toLowerCase();
        let cursor = 0;
        let index = lowerText.indexOf(lowerQuery, cursor);
        while (index !== -1) {
            if (index > cursor) {
                fragment.appendChild(document.createTextNode(text.slice(cursor, index)));
            }
            const mark = document.createElement('mark');
            mark.className = 'search-highlight';
            mark.textContent = text.slice(index, index + query.length);
            fragment.appendChild(mark);
            cursor = index + query.length;
            index = lowerText.indexOf(lowerQuery, cursor);
        }
        if (cursor < text.length) {
            fragment.appendChild(document.createTextNode(text.slice(cursor)));
        }
        return fragment;
    }

    // 跳轉到搜尋結果後，把目前渲染出來的訊息串裡符合搜尋關鍵字的文字節點換成搜尋標記
    // （只處理純文字節點、跳過 <a> 連結內部，標記所有符合之處）
    function highlightQueryInMessageList(query) {
        if (!query) {
            return;
        }
        const lowerQuery = query.toLowerCase();
        for (const contentEl of els.messageList.querySelectorAll('.bubble > div')) {
            const walker = document.createTreeWalker(contentEl, NodeFilter.SHOW_TEXT);
            const textNodes = [];
            let node = walker.nextNode();
            while (node) {
                if (!node.parentElement?.closest('a')) {
                    textNodes.push(node);
                }
                node = walker.nextNode();
            }
            for (const textNode of textNodes) {
                const text = textNode.textContent;
                if (!text.toLowerCase().includes(lowerQuery)) {
                    continue;
                }
                textNode.replaceWith(buildHighlightedFragment(text, query));
            }
        }
    }

    function buildReadyContentNode(messageType, contentId, fileName) {
        const url = `api/messages/${contentId}/content`;

        if (messageType === 'sticker') {
            const img = document.createElement('img');
            img.className = 'msg-sticker';
            img.loading = 'lazy';
            img.alt = '(貼圖)';
            img.src = url;
            img.addEventListener('error', () => {
                img.replaceWith(buildStickerFallbackNode({ text: '(貼圖)' }));
            }, { once: true });
            return img;
        }

        if (messageType === 'image') {
            const img = document.createElement('img');
            img.className = 'msg-image';
            img.loading = 'lazy';
            img.src = url;
            img.alt = '圖片';
            img.addEventListener('click', () => openImageModal(url));
            return img;
        }
        if (messageType === 'video') {
            const video = document.createElement('video');
            video.className = 'msg-video';
            video.controls = true;
            video.preload = 'none';
            video.src = url;
            return video;
        }
        if (messageType === 'audio') {
            const audio = document.createElement('audio');
            audio.className = 'msg-audio';
            audio.controls = true;
            audio.preload = 'none';
            audio.src = url;
            return audio;
        }
        if (messageType === 'file') {
            const wrap = document.createElement('div');
            wrap.className = 'msg-file';
            const icon = document.createElement('span');
            icon.textContent = '📄';
            wrap.appendChild(icon);
            const link = document.createElement('a');
            link.href = url;
            link.textContent = fileName || '下載檔案';
            link.target = '_blank';
            link.rel = 'noopener';
            wrap.appendChild(link);
            return wrap;
        }

        const fallback = document.createElement('div');
        fallback.textContent = '（不支援的訊息類型）';
        return fallback;
    }

    // Downloading（已被某個 worker 認領、正在寫入 blob，見 DbContentWorkSource.CompleteAsync）
    // 對使用者來說跟 Pending 是同一件事：都還沒抓完，只是 Pending 還沒開始、Downloading
    // 已經在做了，畫面上沒有必要區分，都顯示同一個「內容抓取中…」節點並繼續輪詢
    function isDownloadInProgress(downloadStatus) {
        return downloadStatus === 'Pending' || downloadStatus === 'Downloading';
    }

    function buildPendingNode(contentId, messageType, fileName) {
        const wrap = document.createElement('div');
        wrap.className = 'msg-pending';
        wrap.dataset.contentId = String(contentId);
        wrap.dataset.messageType = messageType;
        wrap.dataset.fileName = fileName || '';

        const spinner = document.createElement('span');
        spinner.className = 'spinner-border spinner-border-sm';
        spinner.setAttribute('role', 'status');
        wrap.appendChild(spinner);

        const label = document.createElement('span');
        label.textContent = '內容抓取中…';
        wrap.appendChild(label);

        return wrap;
    }

    function buildFailedNode() {
        const wrap = document.createElement('div');
        wrap.className = 'msg-failed';
        wrap.textContent = '⚠ 內容抓取失敗';
        return wrap;
    }

    function buildStickerFallbackNode(message) {
        const div = document.createElement('div');
        div.textContent = message.text ?? '(貼圖)';
        return div;
    }

    // 改版前收到的貼圖沒有 stickerId（LINE 不提供舊訊息回溯查詢），一律走文字 fallback；
    // 有 ID 的話載入 LINE 公開貼圖 CDN，圖片本身失敗（例如該貼圖已下架）也 fallback 回文字
    function buildStickerNode(message) {
        if (!message.stickerId) {
            return buildStickerFallbackNode(message);
        }

        const content = message.content;
        if (!content) {
            return buildStickerFallbackNode(message);
        }

        if (isDownloadInProgress(content.downloadStatus)) {
            return buildPendingNode(content.id, message.messageType, '');
        }
        if (content.downloadStatus === 'Failed') {
            return buildStickerFallbackNode(message);
        }

        return buildReadyContentNode(message.messageType, content.id, '');
    }

    function buildContentNode(message) {
        const type = message.messageType;

        if (type === 'text') {
            const div = document.createElement('div');
            appendLinkifiedText(div, message.text ?? '');
            const matched = matchedHighlightKeywords(message);
            if (matched.length > 0) {
                applyHighlightKeywordsToContainer(div, matched);
            }
            return div;
        }
        if (type === 'sticker') {
            return buildStickerNode(message);
        }

        const content = message.content;
        if (!content) {
            const div = document.createElement('div');
            div.textContent = message.text ?? '';
            return div;
        }

        if (isDownloadInProgress(content.downloadStatus)) {
            return buildPendingNode(content.id, type, content.fileName);
        }
        if (content.downloadStatus === 'Failed') {
            return buildFailedNode();
        }
        return buildReadyContentNode(type, content.id, content.fileName);
    }

    function createMessageRow(message, showAvatarAndName) {
        const row = document.createElement('div');
        row.className = 'message-row' + (showAvatarAndName ? ' show-avatar' : '');
        row.dataset.messageId = String(message.id);

        const avatar = buildAvatarElement('', {
            pictureUrl: message.pictureUrl,
            iconKey: message.avatarIcon,
            colorSeed: message.userId || message.displayName || '?'
        });
        avatar.title = message.userId ? `${message.displayName}（${message.userId}）` : message.displayName;

        if (message.userId) {
            const consumeAvatarClick = attachContextMenuTriggers(
                avatar, (e) => showAvatarContextMenu(e, message));
            avatar.addEventListener('click', (e) => {
                if (consumeAvatarClick()) {
                    e.preventDefault();
                    e.stopPropagation();
                }
            });
        }

        row.appendChild(avatar);

        const group = document.createElement('div');
        group.className = 'message-group';

        if (showAvatarAndName) {
            const name = document.createElement('div');
            name.className = 'message-sender-name';
            name.textContent = message.displayName;
            if (message.userId) {
                name.title = message.userId;
            }
            group.appendChild(name);
        }

        const isHigh = isHighlighted(message);
        const bubble = document.createElement('div');
        bubble.className = 'bubble'
            + (message.messageType === 'sticker' ? ' sticker' : '')
            + (showAvatarAndName ? ' has-tail' : '')
            + (isHigh ? ' highlighted' : '');
        bubble.appendChild(buildContentNode(message));

        // LINE 的時間戳貼在泡泡外側，不是泡泡裡面
        const time = document.createElement('span');
        time.className = 'msg-time';
        time.textContent = formatTime(message.eventTimestamp);

        const bubbleRow = document.createElement('div');
        bubbleRow.className = 'bubble-row';
        bubbleRow.appendChild(bubble);
        bubbleRow.appendChild(time);

        group.appendChild(bubbleRow);
        row.appendChild(group);

        if (message.content && isDownloadInProgress(message.content.downloadStatus)) {
            state.pendingContentIds.add(message.content.id);
        }

        return row;
    }

    function appendMessages(messages, animate) {
        const list = els.messageList;
        for (const message of messages) {
            state.messagesCache.set(message.id, { text: message.text, userId: message.userId });
            const key = dateKey(message.eventTimestamp);
            if (key !== state.lastAppendedDateKey) {
                list.appendChild(createDateSeparator(message.eventTimestamp));
                state.lastAppendedDateKey = key;
                state.lastAppendedSenderId = null;
            }

            const showAvatarAndName = message.userId !== state.lastAppendedSenderId;
            state.lastAppendedSenderId = message.userId;

            const row = createMessageRow(message, showAvatarAndName);
            if (animate) {
                row.classList.add('message-enter');
            }
            list.appendChild(row);
        }
    }

    function prependMessages(messages) {
        const list = els.messageList;
        // 截斷提示只屬於「目前視窗的最頂端」；往前加載之後它就不再是最頂端了，先摘掉。
        // 不摘的話 notice 會被壓在新插入的舊訊息下面（語意變成對話中間有缺口），
        // 而且下面用 firstChild.dataset.dateKey 做的日期分隔線去重會因為 notice 沒有
        // dateKey 而整段失效，接縫處會冒出兩條同一天的分隔線。
        list.querySelector(':scope > .truncated-notice')?.remove();

        const previousScrollHeight = list.scrollHeight;
        const previousScrollTop = list.scrollTop;
        // 銜接處判斷用：往前加載的這批訊息接上既有清單頂端時，如果兩邊同一天，
        // 既有清單頂端那個日期分隔線就是多的（新批次結尾也會插一個同一天的）
        const previousFirstChild = list.firstChild;
        const previousFirstDateKey = previousFirstChild instanceof HTMLElement
            ? previousFirstChild.dataset.dateKey ?? null
            : null;

        const fragment = document.createDocumentFragment();
        let lastDateKey = null;
        let lastSenderId = null;
        for (const message of messages) {
            state.messagesCache.set(message.id, { text: message.text, userId: message.userId });
            const key = dateKey(message.eventTimestamp);
            if (key !== lastDateKey) {
                fragment.appendChild(createDateSeparator(message.eventTimestamp));
                lastDateKey = key;
                lastSenderId = null;
            }
            const showAvatarAndName = message.userId !== lastSenderId;
            lastSenderId = message.userId;
            fragment.appendChild(createMessageRow(message, showAvatarAndName));
        }

        list.insertBefore(fragment, list.firstChild);

        if (previousFirstDateKey !== null && lastDateKey === previousFirstDateKey) {
            previousFirstChild.remove();
        }

        list.scrollTop = previousScrollTop + (list.scrollHeight - previousScrollHeight);
    }

    function updateContentNode(status) {
        const pendingEl = els.messageList.querySelector(`.msg-pending[data-content-id="${status.contentId}"]`);
        if (!pendingEl) {
            return;
        }
        const replacement = status.downloadStatus === 'Completed'
            ? buildReadyContentNode(pendingEl.dataset.messageType, status.contentId, pendingEl.dataset.fileName)
            : (pendingEl.dataset.messageType === 'sticker' 
                ? buildStickerFallbackNode({ text: '(貼圖)' }) 
                : buildFailedNode());
        pendingEl.replaceWith(replacement);
    }

    // === 圖片燈箱：預設縮到符合版面，點擊在「符合版面／原尺寸」間切換 ===

    function openImageModal(url) {
        const img = els.imageModalImg;
        img.classList.remove('original-size', 'no-zoom');
        els.imageModalBody.classList.remove('zoomed');

        // 圖片本身比視窗小的話「原尺寸」跟「符合版面」看起來一樣，沒有東西可以放大，
        // 游標不該做出可點擊的暗示；函式本身是純粹重算，重複呼叫沒有副作用
        const applyZoomGuard = () => {
            const fitsAlready = img.naturalWidth <= els.imageModalBody.clientWidth
                && img.naturalHeight <= els.imageModalBody.clientHeight;
            img.classList.toggle('no-zoom', fitsAlready);
        };

        img.onload = applyZoomGuard;
        img.src = url;
        // 一定要先 show() 讓 modal 進入版面，modal-body 的 clientWidth/Height 才量得到正確值；
        // 這裡量到的還是隱藏狀態的 0×0，fitsAlready 永遠算不出來
        bootstrap.Modal.getOrCreateInstance(els.imageModal).show();

        // 連續開啟「同一張」圖片時 src 沒變，不同瀏覽器對這種情況下 onload/complete/decode()
        // 該不該再觸發一次的語意實測並不一致，三種訊號一起掛，哪個先來就套用，比賭中某一個更穩
        img.decode().then(applyZoomGuard).catch(() => {});
        requestAnimationFrame(() => requestAnimationFrame(applyZoomGuard));
    }

    function toggleImageZoom() {
        const img = els.imageModalImg;
        if (img.classList.contains('no-zoom')) {
            return;
        }
        const zoomed = img.classList.toggle('original-size');
        els.imageModalBody.classList.toggle('zoomed', zoomed);
    }

    // === 置底跟隨 ===

    function isNearBottom() {
        const list = els.messageList;
        return list.scrollHeight - list.scrollTop - list.clientHeight < NEAR_BOTTOM_THRESHOLD_PX;
    }

    function isNearTop() {
        return els.messageList.scrollTop < NEAR_TOP_THRESHOLD_PX;
    }

    function scrollToBottom(smooth) {
        els.messageList.scrollTo({ top: els.messageList.scrollHeight, behavior: smooth ? 'smooth' : 'auto' });
    }

    function updateFollowUi() {
        // 歷史檢視期間浮動鈕讓位給 historical-banner 的「回到最新」，兩個「回到最新」入口同時
        // 出現會讓人搞不清楚差別
        els.scrollBottomBtn.classList.toggle('d-none', state.following || state.historicalView);
        els.unreadBadge.classList.toggle('d-none', state.unreadCount === 0);
        els.unreadBadge.textContent = String(state.unreadCount);
    }

    function updateHistoricalBanner() {
        els.historicalBanner.classList.toggle('d-none', !state.historicalView);
    }

    function setFollowing(following) {
        state.following = following;
        if (following) {
            state.unreadCount = 0;
        }
        updateFollowUi();
    }

    // === 側欄：群組列表與全螢幕群組橫列 ===

    // 取得群組未讀 badge 文字。規則：unreadCount > 0 且不是目前群組才顯示，超過 99 顯示 99+
    function getUnreadBadgeText(group, currentGroupId = state.groupId) {
        if (!group || !(group.unreadCount > 0) || group.groupId === currentGroupId) {
            return null;
        }
        return group.unreadCount > 99 ? '99+' : String(group.unreadCount);
    }

    function renderFullscreenGroupBar() {
        if (!els.fullscreenGroupBar) {
            return;
        }
        els.fullscreenGroupBar.innerHTML = '';
        for (const group of state.groups) {
            const btn = document.createElement('button');
            btn.type = 'button';
            btn.className = 'fullscreen-group-item' + (group.groupId === state.groupId ? ' active' : '');
            btn.dataset.groupId = group.groupId;
            btn.title = group.displayName;
            btn.setAttribute('aria-label', group.displayName);

            const avatar = buildAvatarElement('fullscreen-group-avatar', {
                pictureUrl: group.pictureUrl,
                iconKey: 'group',
                isGroup: true
            });
            btn.appendChild(avatar);

            const badgeText = getUnreadBadgeText(group, state.groupId);
            if (badgeText) {
                const badge = document.createElement('span');
                badge.className = 'fullscreen-group-badge';
                badge.textContent = badgeText;
                btn.appendChild(badge);
            }

            btn.addEventListener('click', () => {
                selectGroup(group.groupId);
            });

            els.fullscreenGroupBar.appendChild(btn);
        }
    }

    function renderGroupList(filterText) {
        const filter = (filterText || '').trim().toLowerCase();
        const groups = filter
            ? state.groups.filter(g => g.displayName.toLowerCase().includes(filter))
            : state.groups;

        els.groupList.innerHTML = '';

        if (state.groups.length === 0) {
            const empty = document.createElement('div');
            empty.className = 'group-list-empty';
            empty.textContent = '尚無群組資料';
            els.groupList.appendChild(empty);
            if (state.fullscreen && els.fullscreenGroupBar) {
                const prevScrollLeft = els.fullscreenGroupBar.scrollLeft;
                renderFullscreenGroupBar();
                els.fullscreenGroupBar.scrollLeft = prevScrollLeft;
            }
            return;
        }

        if (groups.length === 0) {
            const empty = document.createElement('div');
            empty.className = 'group-list-empty';
            empty.textContent = '找不到符合的群組';
            els.groupList.appendChild(empty);
            if (state.fullscreen && els.fullscreenGroupBar) {
                const prevScrollLeft = els.fullscreenGroupBar.scrollLeft;
                renderFullscreenGroupBar();
                els.fullscreenGroupBar.scrollLeft = prevScrollLeft;
            }
            return;
        }

        for (const group of groups) {
            els.groupList.appendChild(createGroupItem(group));
        }

        if (state.fullscreen && els.fullscreenGroupBar) {
            const prevScrollLeft = els.fullscreenGroupBar.scrollLeft;
            renderFullscreenGroupBar();
            els.fullscreenGroupBar.scrollLeft = prevScrollLeft;
        }
    }

    function createGroupItem(group) {
        const btn = document.createElement('button');
        btn.type = 'button';
        btn.className = 'group-item' + (group.groupId === state.groupId ? ' active' : '');
        btn.dataset.groupId = group.groupId;

        const avatar = buildAvatarElement('group-avatar', {
            pictureUrl: group.pictureUrl,
            iconKey: 'group',
            isGroup: true
        });
        btn.appendChild(avatar);

        const text = document.createElement('div');
        text.className = 'group-item-text';

        const name = document.createElement('div');
        name.className = 'group-item-name';
        name.textContent = group.displayName;
        text.appendChild(name);

        if (group.lastMessagePreview) {
            const preview = document.createElement('div');
            preview.className = 'group-item-preview';
            preview.textContent = group.lastMessagePreview;
            text.appendChild(preview);
        }

        btn.appendChild(text);

        // 收合成窄欄時整列文字會隱藏、只剩頭貼，用原生 title 當 tooltip 顯示群組名
        // （自製 tooltip 會被群組列表的 overflow 裁掉）
        btn.title = group.displayName;

        // 右側直欄：上面是最後訊息時間、下面是未讀 badge
        const meta = document.createElement('div');
        meta.className = 'group-item-meta';

        if (group.lastMessageAt) {
            const time = document.createElement('div');
            time.className = 'group-item-time';
            time.textContent = formatListTime(group.lastMessageAt);
            meta.appendChild(time);
        }

        // 正在看的群組視為已讀，不顯示 badge；其餘顯示未讀數，上限 99+
        const badgeText = getUnreadBadgeText(group, state.groupId);
        if (badgeText) {
            const badge = document.createElement('span');
            badge.className = 'group-item-badge';
            badge.textContent = badgeText;
            meta.appendChild(badge);
        }

        btn.appendChild(meta);

        const consumeGroupClick = attachContextMenuTriggers(
            btn, (e) => showGroupContextMenu(e, group));

        btn.addEventListener('click', (e) => {
            if (consumeGroupClick()) {
                e.preventDefault();
                e.stopPropagation();
                return;
            }
            selectGroup(group.groupId);
            els.chatApp.classList.add('mobile-chat-open');
        });

        return btn;
    }

    function updateActiveGroupItem() {
        for (const item of els.groupList.querySelectorAll('.group-item')) {
            const isActive = item.dataset.groupId === state.groupId;
            item.classList.toggle('active', isActive);
            // 選取中的群組不顯示未讀 badge（開啟即已讀），立即把既有 badge 拿掉
            if (isActive) {
                item.querySelector('.group-item-badge')?.remove();
            }
        }
        if (els.fullscreenGroupBar) {
            for (const item of els.fullscreenGroupBar.querySelectorAll('.fullscreen-group-item')) {
                const isActive = item.dataset.groupId === state.groupId;
                item.classList.toggle('active', isActive);
                if (isActive) {
                    item.querySelector('.fullscreen-group-badge')?.remove();
                }
            }
        }
    }

    function updateChatHeader(group) {
        els.chatHeaderAvatar.replaceWith(buildAvatarElement('chat-header-avatar', {
            pictureUrl: group?.pictureUrl,
            iconKey: 'group',
            isGroup: true
        }));
        els.chatHeaderAvatar = els.chatApp.querySelector('.chat-header-avatar');

        els.chatHeaderName.textContent = group?.displayName ?? '選擇一個群組';
        els.chatHeaderMembers.textContent = group && group.memberCount > 0 ? `(${group.memberCount})` : '';
    }

    // === 訊息搜尋 ===

    const SEARCH_DEBOUNCE_MS = 300;
    let searchDebounceTimer = null;

    function openSearchPanel() {
        els.searchPanel.classList.remove('d-none');
        els.searchInput.focus();
    }

    function closeSearchPanel() {
        els.searchPanel.classList.add('d-none');
        els.searchInput.value = '';
        els.searchResults.innerHTML = '';
        // 讓還在飛的搜尋回應回來時發現對不上而被丟棄，不會在關閉後突然補畫結果
        state.searchRequestToken++;
    }

    function scheduleSearch() {
        clearTimeout(searchDebounceTimer);
        searchDebounceTimer = setTimeout(runSearch, SEARCH_DEBOUNCE_MS);
    }

    async function runSearch() {
        const query = els.searchInput.value.trim();
        if (!query) {
            els.searchResults.innerHTML = '';
            return;
        }

        const token = ++state.searchRequestToken;
        let url = `api/messages/search?q=${encodeURIComponent(query)}`;
        if (state.searchScope === 'group' && state.groupId) {
            url += `&groupId=${encodeURIComponent(state.groupId)}`;
        }

        let response;
        try {
            response = await fetchJson(url);
        } catch {
            return;
        }
        if (token !== state.searchRequestToken) {
            return;
        }
        renderSearchResults(response.results, response.limit, query);
    }

    function renderSearchResults(results, limit, query) {
        els.searchResults.innerHTML = '';

        if (limit) {
            const notice = document.createElement('div');
            notice.className = 'search-encryption-notice';
            let message = `訊息內容已加密，僅搜尋最近 ${limit.windowDays} 天的文字訊息。`;
            if (limit.candidateCapped) {
                message += `已達 ${SEARCH_CANDIDATE_LIMIT} 則候選上限，可指定單一群組縮小範圍。`;
            }
            notice.textContent = message;
            els.searchResults.appendChild(notice);
        }

        if (results.length === 0) {
            const empty = document.createElement('div');
            empty.className = 'search-result-empty';
            empty.textContent = '找不到符合的訊息';
            els.searchResults.appendChild(empty);
            return;
        }

        for (const result of results) {
            els.searchResults.appendChild(createSearchResultItem(result, query));
        }
    }

    function createSearchResultItem(result, query) {
        const btn = document.createElement('button');
        btn.type = 'button';
        btn.className = 'search-result-item';

        const meta = document.createElement('div');
        meta.className = 'search-result-meta';

        const groupLabel = document.createElement('span');
        groupLabel.textContent = result.groupDisplayName;
        meta.appendChild(groupLabel);

        const sep = document.createElement('span');
        sep.textContent = '・';
        meta.appendChild(sep);

        const nameLabel = document.createElement('span');
        nameLabel.className = 'search-result-name';
        nameLabel.appendChild(buildHighlightedFragment(result.displayName, query));
        meta.appendChild(nameLabel);

        const time = document.createElement('span');
        time.className = 'search-result-time';
        time.textContent = formatListTime(result.eventTimestamp);
        meta.appendChild(time);

        btn.appendChild(meta);

        const snippet = document.createElement('div');
        snippet.className = 'search-result-snippet';
        snippet.appendChild(buildHighlightedFragment(result.snippet, query));
        btn.appendChild(snippet);

        btn.addEventListener('click', () => jumpToSearchResult(result, query));
        return btn;
    }

    async function jumpToSearchResult(result, query) {
        const token = ++state.requestToken;

        if (result.groupId !== state.groupId) {
            state.groupId = result.groupId;
            updateActiveGroupItem();
            updateChatHeader(state.groups.find(g => g.groupId === result.groupId));
            els.chatApp.classList.add('mobile-chat-open');
        }

        state.oldestId = null;
        state.windowNewestId = null;
        state.hasMoreOlder = false;
        state.historicalView = true;
        state.noMoreNewer = false;
        state.noMoreNewerAt = null;
        updateHistoricalBanner();
        updateLoadNewerButton();
        setFollowing(true);
        clearMessageList();
        updateLoadMoreButton();
        closeSearchPanel();

        try {
            const page = await fetchJson(
                `api/groups/${encodeURIComponent(result.groupId)}/messages?aroundId=${result.messageId}&days=${INITIAL_DAYS}`);
            if (token !== state.requestToken) {
                return;
            }
            appendMessages(page.messages, false);
            if (page.truncated) {
                els.messageList.insertBefore(createTruncatedNotice(), els.messageList.firstChild);
            }
            if (page.messages.length > 0) {
                state.oldestId = page.messages[0].id;
                state.windowNewestId = page.messages[page.messages.length - 1].id;
            }
            state.hasMoreOlder = page.hasMore;
            updateLoadMoreButton();
            updateLoadNewerButton();
            highlightQueryInMessageList(query);

            const targetRow = els.messageList.querySelector(`[data-message-id="${result.messageId}"]`);
            if (targetRow) {
                targetRow.scrollIntoView({ block: 'center' });
                targetRow.classList.add('message-highlight-flash');
                setTimeout(() => targetRow.classList.remove('message-highlight-flash'), 1600);
            }
            setConnectionOk(true);
        } catch {
            if (token === state.requestToken) {
                setConnectionOk(false);
            }
        }
    }

    // === 資料載入 ===

    async function loadGroups() {
        const groups = await fetchJson('api/groups/list', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(readRequestBody())
        });
        state.groups = groups;
        seedReadStateForNewGroups(groups);
        renderGroupList(els.groupSearch.value);

        if (groups.length === 0) {
            state.initialEmpty = true;
            return;
        }

        await selectGroup(groups[0].groupId);
    }

    // loadGroups() 只在頁面載入時跑一次；之後靠這支輪詢讓側欄（新群組/預覽/排序）
    // 定期跟資料庫同步，不然使用者在別的群組發言，側欄要重新整理才會出現。
    // 分頁隱藏時跳過，省掉背景分頁的無謂請求
    async function pollGroups() {
        if (document.hidden) {
            return;
        }
        await refreshGroupList();
    }

    // 側欄清單的實際刷新。刪除群組／清空訊息這種「使用者剛按下去」的操作直接呼叫這支，
    // 不能走 pollGroups——那支在分頁被判定為隱藏時會整個跳過，畫面會留著已經不存在的群組
    async function refreshGroupList() {
        if (state.groupsPolling) {
            // 剛好有一次 10 秒輪詢在飛就排隊，等它結束再跑一次。直接 return 會讓
            // 「刪除完成」的刷新被靜默丟掉，已刪除的群組會繼續留在側欄最多 10 秒
            state.groupsRefreshQueued = true;
            return;
        }
        state.groupsPolling = true;
        try {
            const groups = await fetchJson('api/groups/list', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify(readRequestBody())
            });
            state.groups = groups;

            // 只有在請求成功回傳後才做判斷：若目前開著的群組不在清單中，代表在別台裝置被刪除或訊息被清空
            if (state.groupId !== null && !groups.some(g => g.groupId === state.groupId)) {
                resetChatPanel();
                showToast('目前的群組已不存在或訊息已被清空');
            }

            // 側欄每 10 秒更新一次；latch 生效後只要該群組的 lastMessageId 真的變了（有新訊息，
            // 或後端把漂移的值修正回來），就解除 latch 讓「載入更新」重新可用。比對的是「值有沒有變」
            // 而不是「有沒有大於 windowNewestId」——漂移情境下後者恆為真，latch 會每 10 秒被解除一次
            // 而失去意義。
            if (state.noMoreNewer) {
                const currentGroup = groups.find(g => g.groupId === state.groupId);
                if (currentGroup && currentGroup.lastMessageId !== state.noMoreNewerAt) {
                    state.noMoreNewer = false;
                    state.noMoreNewerAt = null;
                }
            }
            seedReadStateForNewGroups(groups);

            const previousScrollTop = els.groupList.scrollTop;
            renderGroupList(els.groupSearch.value);
            els.groupList.scrollTop = previousScrollTop;

            if (state.initialEmpty && groups.length > 0) {
                state.initialEmpty = false;
                // 啟動時資料庫還沒有任何群組，之後第一筆訊息進來就自動帶使用者進去，
                // 不必自己重新整理頁面才看得到
                await selectGroup(groups[0].groupId);
            }

            // 有選取群組時，連線狀態由 pollNewer 負責回報；兩條輪詢各自回報
            // 會在「一好一壞」的交錯下讓連線橫幅閃爍。只有還沒選到任何群組
            // （例如空資料庫剛啟動）時，這支輪詢才是唯一對外的請求，需要它來回報
            if (state.groupId === null) {
                setConnectionOk(true);
            }
        } catch {
            if (state.groupId === null) {
                setConnectionOk(false);
            }
        } finally {
            state.groupsPolling = false;
        }

        if (state.groupsRefreshQueued) {
            state.groupsRefreshQueued = false;
            await refreshGroupList();
        }
    }

    async function selectGroup(groupId) {
        const token = ++state.requestToken;

        state.groupId = groupId;
        state.oldestId = null;
        state.newestId = null;
        state.windowNewestId = null;
        state.daysWindow = INITIAL_DAYS;
        state.hasMoreOlder = false;
        state.historicalView = false;
        state.noMoreNewer = false;
        state.noMoreNewerAt = null;
        updateHistoricalBanner();
        updateLoadNewerButton();
        setFollowing(true);
        clearMessageList();

        // 一進群組就把已讀基準推到目前已知的最後一則，側欄該群組的未讀 badge 立即清掉，
        // 不必等訊息載入完成或下一輪輪詢
        const selected = state.groups.find(g => g.groupId === groupId);
        if (selected) {
            markGroupRead(groupId, selected.lastMessageId);
        }
        updateActiveGroupItem();
        updateChatHeader(selected);
        updateLoadMoreButton();
        updateLoadNewerButton();

        try {
            const page = await fetchJson(
                `api/groups/${encodeURIComponent(groupId)}/messages?days=${state.daysWindow}`);
            if (token !== state.requestToken) {
                return;
            }
            renderWindow(page);
            setConnectionOk(true);
        } catch {
            if (token === state.requestToken) {
                setConnectionOk(false);
            }
        }
    }

    function clearMessageList() {
        els.messageList.innerHTML = '';
        state.messagesCache.clear();
        state.pendingContentIds.clear();
        state.lastAppendedDateKey = null;
        state.lastAppendedSenderId = null;
    }

    /// 用一次完整的天數視窗查詢結果重繪整個清單（初次載入、以及沒有游標可用時的「載入更早」都走這裡）
    function renderWindow(page) {
        clearMessageList();
        appendMessages(page.messages, false);
        if (page.truncated) {
            els.messageList.insertBefore(createTruncatedNotice(), els.messageList.firstChild);
        }
        if (page.messages.length > 0) {
            state.oldestId = page.messages[0].id;
        }
        state.newestId = page.latestId ?? state.newestId;
        // 開著的群組視為已讀（LINE 慣例），把已讀基準推進到目前視窗最後一則
        markGroupRead(state.groupId, state.newestId);
        state.hasMoreOlder = page.hasMore;
        // 先捲到底再判斷膠囊要不要顯示，不然這裡量到的還是捲動前（頂部）的位置，
        // 「沒有更早的訊息」會在捲到底之前閃一下才消失
        scrollToBottom(false);
        updateLoadMoreButton();
    }

    function updateLoadMoreButton() {
        els.loadMoreBtn.disabled = !state.hasMoreOlder;
        els.loadMoreBtn.textContent = state.hasMoreOlder ? '載入更早 7 天' : '沒有更早的訊息';
        // 「載入更早」是操作入口，常駐；「沒有更早的訊息」只是告知狀態，捲到畫面中間看到很突兀，
        // 只在捲到最頂部附近時才顯示
        els.loadMoreBtn.classList.toggle('d-none', !state.hasMoreOlder && !isNearTop());
    }

    function updateLoadNewerButton() {
        els.loadNewerBtn.classList.toggle('d-none', !state.historicalView);
        els.loadNewerBtn.disabled = state.loadingNewer;
    }

    async function loadOlder() {
        if (state.loadingOlder || !state.hasMoreOlder) {
            return;
        }
        const token = state.requestToken;
        // 目前視窗內一則訊息都沒有（但更早還有歷史）時沒有游標可用，改成把天數視窗往前拉並重繪；
        // 否則按鈕看起來可按、按下去卻什麼都不會發生
        const growWindow = state.oldestId == null;
        if (growWindow && state.daysWindow >= MAX_DAYS_WINDOW) {
            state.hasMoreOlder = false;
            updateLoadMoreButton();
            return;
        }

        state.loadingOlder = true;
        els.loadMoreBtn.disabled = true;
        try {
            const url = growWindow
                ? `api/groups/${encodeURIComponent(state.groupId)}/messages?days=${state.daysWindow + LOAD_MORE_DAYS}`
                : `api/groups/${encodeURIComponent(state.groupId)}/messages?beforeId=${state.oldestId}&days=${LOAD_MORE_DAYS}`;
            const page = await fetchJson(url);
            if (token !== state.requestToken) {
                return;
            }

            if (growWindow) {
                state.daysWindow += LOAD_MORE_DAYS;
                renderWindow(page);
            } else {
                if (page.messages.length > 0) {
                    // 這裡不依 page.truncated 重新插入 .truncated-notice 提示：後端在 beforeId 路徑截斷時，
                    // 丟掉的是更久遠的那批訊息，hasMore 仍為 true，下一次按「載入更早」就會接回來，
                    // 中間並無缺口，不需要（也不應該）在此處提示，避免誤導使用者以為對話有缺漏。
                    prependMessages(page.messages);
                    state.oldestId = page.messages[0].id;
                }
                state.hasMoreOlder = page.hasMore;
            }
            setConnectionOk(true);
        } catch {
            setConnectionOk(false);
        } finally {
            state.loadingOlder = false;
            updateLoadMoreButton();
        }
    }

    async function loadNewer() {
        if (state.loadingNewer || !state.historicalView || state.noMoreNewer || state.windowNewestId == null) {
            return;
        }
        const token = state.requestToken;
        state.loadingNewer = true;
        updateLoadNewerButton();
        try {
            const page = await fetchJson(
                `api/groups/${encodeURIComponent(state.groupId)}/messages?afterId=${state.windowNewestId}`);
            if (token !== state.requestToken) {
                return;
            }
            if (page.messages.length > 0) {
                appendMessages(page.messages, false);
                state.windowNewestId = page.messages[page.messages.length - 1].id;
            } else {
                // lastMessageId 漂移（指向已被保留期清除的訊息）時 afterId 會永遠回 0 筆、
                // maybeExitHistoricalView 的條件永遠不成立，沒有這個 latch 的話使用者停在底部時
                // 每個捲動 tick 都會打一次空請求。
                state.noMoreNewer = true;
                state.noMoreNewerAt = state.groups.find(g => g.groupId === state.groupId)?.lastMessageId ?? null;
            }
            maybeExitHistoricalView();
            setConnectionOk(true);
        } catch {
            if (token === state.requestToken) {
                setConnectionOk(false);
            }
        } finally {
            state.loadingNewer = false;
            updateLoadNewerButton();
        }
    }

    // afterId 的回應不帶 latestId，判斷「是否追上最新」只能靠側欄資料（每 10 秒更新一次）。
    // 追上之後要把即時輪詢的基準交棒給 windowNewestId，否則 pollNewer 會從舊的 newestId
    // 重新抓一次，畫面會出現重複訊息
    function maybeExitHistoricalView() {
        const group = state.groups.find(g => g.groupId === state.groupId);
        if (group == null || group.lastMessageId == null || state.windowNewestId == null) {
            return;
        }
        if (state.windowNewestId >= group.lastMessageId) {
            state.historicalView = false;
            state.newestId = state.windowNewestId;
            markGroupRead(state.groupId, state.newestId);
            updateHistoricalBanner();
            updateLoadNewerButton();
            updateFollowUi();
        }
    }

    async function pollNewer() {
        // 連線慢時輪詢可能比間隔還久，沒有這個旗標的話兩輪會讀到同一個 newestId 而把訊息插兩次
        if (!state.groupId || document.hidden || state.polling || state.loadingOlder) {
            return;
        }
        const token = state.requestToken;
        state.polling = true;
        try {
            // 歷史檢視期間不把新訊息接到視窗尾端（視窗錨定在過去某個時間點，接了會出現時間斷層）；
            // Pending 內容狀態輪詢照常，跟目前看的是不是即時畫面無關
            if (state.newestId != null && !state.historicalView) {
                const page = await fetchJson(
                    `api/groups/${encodeURIComponent(state.groupId)}/messages?afterId=${state.newestId}`);
                if (token !== state.requestToken) {
                    return;
                }
                if (page.messages.length > 0) {
                    const wasFollowing = state.following;
                    appendMessages(page.messages, true);
                    state.newestId = page.messages[page.messages.length - 1].id;
                    // 新訊息接進目前開著的群組，同步推進已讀基準（側欄不會對正在看的群組跳未讀）
                    markGroupRead(state.groupId, state.newestId);
                    if (wasFollowing) {
                        scrollToBottom(true);
                    } else {
                        state.unreadCount += page.messages.length;
                        updateFollowUi();
                    }
                }
            }
            await pollPendingStatuses(token);
            setConnectionOk(true);
        } catch {
            setConnectionOk(false);
        } finally {
            state.polling = false;
        }
    }

    async function pollPendingStatuses(token) {
        if (state.pendingContentIds.size === 0) {
            return;
        }
        const pendingArray = Array.from(state.pendingContentIds);
        for (let i = 0; i < pendingArray.length; i += STATUS_POLL_BATCH_SIZE) {
            const batch = pendingArray.slice(i, i + STATUS_POLL_BATCH_SIZE);
            const ids = batch.join(',');
            const statuses = await fetchJson(`api/messages/statuses?ids=${ids}`);
            if (token !== state.requestToken) {
                return;
            }
            for (const status of statuses) {
                // Downloading 還沒到終態，繼續留在 pendingContentIds 裡讓下一輪輪詢再檢查——
                // 提早移除的話，這則訊息之後不管是變 Completed 還是 Failed 都不會再被撿回來
                // 更新畫面，會永遠卡在轉圈圈的 spinner 節點
                if (!isDownloadInProgress(status.downloadStatus)) {
                    state.pendingContentIds.delete(status.contentId);
                    updateContentNode(status);
                }
            }
        }
    }

    // === 字體大小（存 localStorage，每台裝置各自記，不進 DB） ===

    const FONT_BASE_PX_STORAGE_KEY = 'chat-font-base-px';
    const DEFAULT_FONT_BASE_PX = 20;
    const FONT_BASE_PX_MIN = 8;
    const FONT_BASE_PX_MAX = 28;

    window.messageServiceFont = {
        FONT_BASE_PX_STORAGE_KEY,
        FONT_BASE_PX_MIN,
        FONT_BASE_PX_MAX,
        DEFAULT_FONT_BASE_PX
    };

    // 「中」檔的實際 px 大小，跟設定 modal 的「字體大小」數值輸入共用同一個 localStorage key；
    // 這裡只讀不寫——調整數值的介面在設定 modal，聊天頁的 Aa 選單維持小/中/大三檔切換。
    // 設在 document.documentElement 上（不是 #chat-app）：設定 modal 跟聊天畫面在同一個頁面，
    // 但 modal 在 DOM 裡不是 #chat-app 的子節點，掛在共同的根元素上兩邊才都吃得到
    function applyFontBasePx() {
        let saved;
        try {
            saved = parseInt(localStorage.getItem(FONT_BASE_PX_STORAGE_KEY), 10);
        } catch {
            saved = NaN;
        }
        const px = Number.isFinite(saved) && saved >= FONT_BASE_PX_MIN && saved <= FONT_BASE_PX_MAX
            ? saved
            : DEFAULT_FONT_BASE_PX;
        document.documentElement.style.setProperty('--font-base-px', `${px}px`);
    }

    // === 對話寬度（同樣存 localStorage，每台裝置各自記，不進 DB） ===

    const FULL_WIDTH_STORAGE_KEY = 'chat-full-width';

    // 未勾選時要 removeProperty 而不是設回預設值：預設寬度是由樣式表的媒體查詢決定的
    // （桌面 2/3、手機 75%），一旦在 documentElement 上留下 inline 值就會把手機版也鎖死
    function applyChatWidth() {
        let full = false;
        try {
            full = localStorage.getItem(FULL_WIDTH_STORAGE_KEY) === 'true';
        } catch {
            // localStorage 不可用就用預設寬度
        }
        if (full) {
            document.documentElement.style.setProperty('--bubble-max-width', '100%');
        } else {
            document.documentElement.style.removeProperty('--bubble-max-width');
        }
    }

    function applyFontSize(size) {
        for (const s of FONT_SIZES) {
            els.chatApp.classList.toggle(`font-size-${s}`, s === size);
        }
        for (const btn of els.fontSizeButtons) {
            btn.classList.toggle('active', btn.dataset.fontSize === size);
        }
    }

    function initFontSizeToggle() {
        applyFontBasePx();

        let saved;
        try {
            saved = localStorage.getItem(FONT_SIZE_STORAGE_KEY);
        } catch {
            saved = null;
        }
        applyFontSize(FONT_SIZES.includes(saved) ? saved : DEFAULT_FONT_SIZE);

        for (const btn of els.fontSizeButtons) {
            btn.addEventListener('click', () => {
                const size = btn.dataset.fontSize;
                applyFontSize(size);
                try {
                    localStorage.setItem(FONT_SIZE_STORAGE_KEY, size);
                } catch {
                    // localStorage 不可用（例如無痕模式）就只套用當次畫面，不用另外提示
                }
            });
        }
    }

    // === 側欄未讀數（已讀基準存 localStorage，每台裝置各自記，不進 DB） ===

    const READ_STATE_KEY = 'chat-read-state';

    function loadReadState() {
        try {
            const raw = localStorage.getItem(READ_STATE_KEY);
            const parsed = raw ? JSON.parse(raw) : null;
            // 只收「群組 → 數字」的健康條目，壞資料一律丟掉，不讓它污染後續送出的請求
            if (parsed && typeof parsed === 'object') {
                for (const [groupId, id] of Object.entries(parsed)) {
                    if (typeof id === 'number' && Number.isFinite(id)) {
                        state.readState[groupId] = id;
                    }
                }
            }
        } catch {
            // localStorage 不可用或內容毀損就當作沒有已讀紀錄，未讀數會全部從 0 起算
        }
    }

    function saveReadState() {
        try {
            localStorage.setItem(READ_STATE_KEY, JSON.stringify(state.readState));
        } catch {
            // 無痕模式等寫入失敗就只在本次工作階段記憶，不另外提示
        }
    }

    // 把某群組的已讀基準往前推進到 id（只進不退），並持久化。群組被開啟或有新訊息接進畫面時呼叫
    function markGroupRead(groupId, id) {
        if (!groupId || typeof id !== 'number' || !Number.isFinite(id)) {
            return;
        }
        if (!(state.readState[groupId] >= id)) {
            state.readState[groupId] = id;
            saveReadState();
        }
    }

    // POST /api/groups/list 帶上目前畫面上有出現群組的已讀基準。
    // state.groups 要等第一次 loadGroups 回來才有值，而 loadGroups 自己就得先呼叫這裡——這時不能過濾，
    // 否則一個基準都送不出去，而後端把「沒帶基準的群組」視為全部已讀（見 GroupsController），
    // 症狀是重新整理後整排未讀數歸零、要等下一輪輪詢才回來。readState 本身已由
    // seedReadStateForNewGroups 清成只含現存群組。
    function readRequestBody() {
        const knownGroupIds = new Set(state.groups.map(g => g.groupId));
        const read = Object.fromEntries(
            Object.entries(state.readState)
                .filter(([groupId]) => knownGroupIds.size === 0 || knownGroupIds.has(groupId))
        );
        return { read };
    }

    // 讓已讀基準跟最新的群組清單對齊：
    // 1. 第一次看到的群組（本裝置沒有任何已讀紀錄）直接以最後一則為基準視為已讀，
    //    避免初次開啟整排都跳出一大包未讀；
    // 2. 已經不存在的群組（訊息被保留期清除）把基準一併移除，
    //    請求 body 只帶目前清單中的群組，不會無限累積
    function seedReadStateForNewGroups(groups) {
        let changed = false;
        for (const group of groups) {
            if (!(group.groupId in state.readState)) {
                state.readState[group.groupId] = group.lastMessageId ?? 0;
                changed = true;
            }
        }
        const knownIds = new Set(groups.map(g => g.groupId));
        for (const groupId of Object.keys(state.readState)) {
            if (!knownIds.has(groupId)) {
                delete state.readState[groupId];
                changed = true;
            }
        }
        if (changed) {
            saveReadState();
        }
    }

    // === 側欄拖曳寬度／兩段式收合（狀態存 localStorage，每台裝置各自記，僅桌面版生效） ===

    const SIDEBAR_STATE_KEY = 'chat-sidebar-state';
    const SIDEBAR_WIDTH_KEY = 'chat-sidebar-width';
    const SIDEBAR_STATES = ['expanded', 'rail', 'hidden'];
    const SIDEBAR_MIN_WIDTH = 200;
    const SIDEBAR_MAX_WIDTH = 480;
    const SIDEBAR_DEFAULT_WIDTH = 320;
    const SIDEBAR_RAIL_WIDTH = 72;      // 與 CSS --sidebar-rail-width 對齊
    // 拖曳吸附遲滯：往內拖到 <140 吸附成窄欄，從窄欄往外拖要 >180 才切回展開，
    // 兩個門檻錯開避免在臨界點抖動
    const SIDEBAR_RAIL_SNAP_IN = 140;
    const SIDEBAR_RAIL_SNAP_OUT = 180;
    const SIDEBAR_KEYBOARD_STEP = 16;

    function clampSidebarWidth(px) {
        return Math.min(SIDEBAR_MAX_WIDTH, Math.max(SIDEBAR_MIN_WIDTH, Math.round(px)));
    }

    function setSidebarWidth(px) {
        const width = clampSidebarWidth(px);
        state.sidebarWidth = width;
        els.sidebar.style.setProperty('--sidebar-width', `${width}px`);
        els.sidebarResizer.setAttribute('aria-valuenow', String(width));
    }

    function persistSidebarWidth() {
        try {
            localStorage.setItem(SIDEBAR_WIDTH_KEY, String(state.sidebarWidth));
        } catch {
            // localStorage 不可用就只在本次工作階段記憶
        }
    }

    function applySidebarState(next) {
        state.sidebarState = next;
        els.chatApp.classList.toggle('sidebar-rail', next === 'rail');
        els.chatApp.classList.toggle('sidebar-hidden', next === 'hidden');

        const expanded = next === 'expanded';
        els.sidebarCollapseBtn.setAttribute('aria-expanded', expanded ? 'true' : 'false');
        // 收合鈕只在 expanded／rail 兩態看得到（hidden 時側欄整個消失），label 描述「按下去會怎樣」
        const label = expanded ? '收合為窄欄' : '隱藏群組列表';
        els.sidebarCollapseBtn.setAttribute('aria-label', label);
        els.sidebarCollapseBtn.title = label;

        try {
            localStorage.setItem(SIDEBAR_STATE_KEY, next);
        } catch {
            // localStorage 不可用就只在本次工作階段記憶
        }
    }

    function cycleSidebarCollapse() {
        if (state.sidebarState === 'expanded') {
            applySidebarState('rail');
        } else if (state.sidebarState === 'rail') {
            applySidebarState('hidden');
        } else {
            applySidebarState('expanded');
        }
    }

    function adjustSidebarWidthByKeyboard(delta) {
        // 鍵盤只在展開態調整寬度，不用鍵盤觸發吸附窄欄（收合走收合鈕，避免箭頭鍵誤觸）
        if (state.sidebarState !== 'expanded') {
            return;
        }
        const target = delta === -Infinity ? SIDEBAR_MIN_WIDTH
            : delta === Infinity ? SIDEBAR_MAX_WIDTH
                : state.sidebarWidth + delta;
        setSidebarWidth(target);
        persistSidebarWidth();
    }

    function initSidebarChrome() {
        els.sidebarCollapseBtn = $('sidebar-collapse-btn');
        els.sidebarExpandBtn = $('sidebar-expand-btn');
        els.sidebarResizer = $('sidebar-resizer');

        let savedWidth;
        try {
            savedWidth = parseInt(localStorage.getItem(SIDEBAR_WIDTH_KEY), 10);
        } catch {
            savedWidth = NaN;
        }
        setSidebarWidth(Number.isFinite(savedWidth) ? savedWidth : SIDEBAR_DEFAULT_WIDTH);

        let savedState;
        try {
            savedState = localStorage.getItem(SIDEBAR_STATE_KEY);
        } catch {
            savedState = null;
        }
        applySidebarState(SIDEBAR_STATES.includes(savedState) ? savedState : 'expanded');

        els.sidebarCollapseBtn.addEventListener('click', cycleSidebarCollapse);
        els.sidebarExpandBtn.addEventListener('click', () => applySidebarState('expanded'));

        let dragStartX = 0;
        let dragStartWidth = 0;

        els.sidebarResizer.addEventListener('pointerdown', (e) => {
            // 手機版沒有拖曳分隔線（單欄全螢幕），保險起見再擋一次
            if (window.matchMedia('(max-width: 768px)').matches) {
                return;
            }
            e.preventDefault();
            els.sidebarResizer.setPointerCapture(e.pointerId);
            dragStartX = e.clientX;
            dragStartWidth = state.sidebarState === 'rail' ? SIDEBAR_RAIL_WIDTH : state.sidebarWidth;
            els.chatApp.classList.add('resizing');
        });

        els.sidebarResizer.addEventListener('pointermove', (e) => {
            if (!els.sidebarResizer.hasPointerCapture(e.pointerId)) {
                return;
            }
            const width = dragStartWidth + (e.clientX - dragStartX);
            if (width < SIDEBAR_RAIL_SNAP_IN) {
                // 拖到很窄就即時預覽成窄欄
                if (state.sidebarState !== 'rail') {
                    applySidebarState('rail');
                }
            } else if (state.sidebarState === 'rail') {
                // 從窄欄往外拖，超過 SNAP_OUT 才切回展開（遲滯）
                if (width > SIDEBAR_RAIL_SNAP_OUT) {
                    applySidebarState('expanded');
                    setSidebarWidth(width);
                }
            } else {
                setSidebarWidth(width);
            }
        });

        const endDrag = (e) => {
            if (els.sidebarResizer.hasPointerCapture(e.pointerId)) {
                els.sidebarResizer.releasePointerCapture(e.pointerId);
            }
            els.chatApp.classList.remove('resizing');
            // 只有展開態才把寬度記下來；窄欄的 72px 不覆蓋記憶，回到展開時沿用上次寬度
            if (state.sidebarState === 'expanded') {
                persistSidebarWidth();
            }
        };
        els.sidebarResizer.addEventListener('pointerup', endDrag);
        els.sidebarResizer.addEventListener('pointercancel', endDrag);

        // 雙擊分隔線重設回預設寬度並展開
        els.sidebarResizer.addEventListener('dblclick', () => {
            applySidebarState('expanded');
            setSidebarWidth(SIDEBAR_DEFAULT_WIDTH);
            persistSidebarWidth();
        });

        els.sidebarResizer.addEventListener('keydown', (e) => {
            let handled = true;
            if (e.key === 'ArrowLeft') {
                adjustSidebarWidthByKeyboard(-SIDEBAR_KEYBOARD_STEP);
            } else if (e.key === 'ArrowRight') {
                adjustSidebarWidthByKeyboard(SIDEBAR_KEYBOARD_STEP);
            } else if (e.key === 'Home') {
                adjustSidebarWidthByKeyboard(-Infinity);
            } else if (e.key === 'End') {
                adjustSidebarWidthByKeyboard(Infinity);
            } else {
                handled = false;
            }
            if (handled) {
                e.preventDefault();
            }
        });
    }

    // === 初始化 ===

    async function init() {
        els.chatApp = $('chat-app');
        els.chatPanel = $('chat-panel');
        els.fullscreenGroupBar = $('fullscreen-group-bar');
        els.sidebar = $('sidebar');
        els.groupSearch = $('group-search');
        els.groupList = $('group-list');
        els.mobileBackBtn = $('mobile-back-btn');
        els.chatHeaderAvatar = $('chat-header-avatar');
        els.chatHeaderName = $('chat-header-name');
        els.chatHeaderMembers = $('chat-header-members');
        els.connectionBanner = $('connection-banner');
        els.composerStatusDot = $('composer-status-dot');
        els.composerStatusText = $('composer-status-text');
        els.loadMoreBtn = $('load-more-btn');
        els.loadNewerBtn = $('load-newer-btn');
        els.messageList = $('message-list');
        els.scrollBottomBtn = $('scroll-bottom-btn');
        els.unreadBadge = $('unread-badge');
        els.imageModal = $('image-modal');
        els.imageModalBody = $('image-modal-body');
        els.imageModalImg = $('image-modal-img');
        els.fontSizeButtons = Array.from(document.querySelectorAll('.font-size-toggle [data-font-size]'));
        els.searchToggleBtn = $('search-toggle-btn');
        els.searchPanel = $('search-panel');
        els.searchInput = $('search-input');
        els.searchCloseBtn = $('search-close-btn');
        els.searchResults = $('search-results');
        els.searchScopeButtons = Array.from(document.querySelectorAll('.search-scope-toggle [data-scope]'));
        els.historicalBanner = $('historical-banner');
        els.historicalBackBtn = $('historical-back-btn');
        els.groupDeleteModal = $('group-delete-modal');
        els.groupDeleteModalTitle = $('group-delete-modal-title');
        els.groupDeleteModalCloseBtn = $('group-delete-modal-close-btn');
        els.groupDeleteModalP1 = $('group-delete-modal-p1');
        els.groupDeleteModalP2 = $('group-delete-modal-p2');
        els.groupDeleteModalP3 = $('group-delete-modal-p3');
        els.groupDeleteCancelBtn = $('group-delete-cancel-btn');
        els.groupDeleteConfirmBtn = $('group-delete-confirm-btn');

        initFontSizeToggle();
        applyChatWidth();
        loadReadState();
        initSidebarChrome();

        els.imageModalImg.addEventListener('click', toggleImageZoom);
        // 點空白處（不是圖片本身、也不是關閉鈕）關閉燈箱，補回全螢幕 modal 少掉的「點背景關閉」直覺
        els.imageModalBody.addEventListener('click', (e) => {
            if (e.target === els.imageModalBody) {
                bootstrap.Modal.getInstance(els.imageModal)?.hide();
            }
        });
        els.loadMoreBtn.addEventListener('click', loadOlder);
        els.loadNewerBtn.addEventListener('click', loadNewer);
        els.groupSearch.addEventListener('input', () => renderGroupList(els.groupSearch.value));
        els.mobileBackBtn.addEventListener('click', () => els.chatApp.classList.remove('mobile-chat-open'));
        els.scrollBottomBtn.addEventListener('click', () => {
            setFollowing(true);
            scrollToBottom(true);
        });
        els.messageList.addEventListener('scroll', () => {
            const near = isNearBottom();
            if (near !== state.following) {
                setFollowing(near);
            }
            updateLoadMoreButton();
            if (state.historicalView && isNearBottom()) {
                loadNewer();
            }
        });

        els.searchToggleBtn.addEventListener('click', () => {
            if (els.searchPanel.classList.contains('d-none')) {
                openSearchPanel();
            } else {
                closeSearchPanel();
            }
        });
        els.searchCloseBtn.addEventListener('click', closeSearchPanel);
        els.searchInput.addEventListener('input', scheduleSearch);
        els.searchInput.addEventListener('keydown', (e) => {
            if (e.key === 'Escape') {
                closeSearchPanel();
            }
        });
        for (const btn of els.searchScopeButtons) {
            btn.addEventListener('click', () => {
                if (btn.dataset.scope === state.searchScope) {
                    return;
                }
                state.searchScope = btn.dataset.scope;
                for (const other of els.searchScopeButtons) {
                    other.classList.toggle('active', other === btn);
                }
                if (els.searchInput.value.trim()) {
                    scheduleSearch();
                }
            });
        }
        els.historicalBackBtn.addEventListener('click', () => selectGroup(state.groupId));

        attachContextMenuTriggers(els.chatPanel, (e) => showPanelContextMenu(e), isExcludedContextTarget);

        document.addEventListener('keydown', (e) => {
            if (e.key !== 'Escape' || !state.fullscreen) {
                return;
            }
            if (activeContextMenu) {
                return;
            }
            if (document.querySelector('.modal.show')) {
                return;
            }
            if (els.searchPanel && !els.searchPanel.classList.contains('d-none')) {
                closeSearchPanel();
                e.preventDefault();
                return;
            }
            e.preventDefault();
            exitFullscreen();
        });

        // 設定 modal 關掉時，如果這次開啟期間真的改了東西（名稱顯示模式、關鍵字規則等），
        // settings.js 會發這個事件——重新載入目前群組的訊息視窗＋側欄，不用使用者自己重新整理
        document.addEventListener('messageservice:settings-changed', async () => {
            await loadHighlightRules();
            applyHighlightVisualSettings();
            if (state.groupId) {
                selectGroup(state.groupId);
            }
            pollGroups();
        });

        applyHighlightVisualSettings();
        await loadHighlightRules();
        loadGroups().catch(() => setConnectionOk(false));
        setInterval(pollNewer, POLL_INTERVAL_MS);
        setInterval(pollGroups, GROUP_POLL_INTERVAL_MS);
        checkDatabaseFallback();
    }

    // SQLite 救援模式的全站警示（需求2）：救場是啟動當下決定、行程存續期間不變的狀態
    // （見 DatabaseStartupDecision），頁面載入時查一次就夠，不用輪詢。查詢失敗就安靜跳過——
    // 這只是提醒層，不該因為它把聊天頁弄壞
    async function checkDatabaseFallback() {
        try {
            const response = await fetch('api/settings/database-status');
            if (!response.ok) {
                return;
            }
            const status = await response.json();
            if (status.sqliteFallbackActive) {
                const banner = $('db-fallback-banner');
                banner.textContent =
                    `目前以 SQLite 救援模式運作——設定的 SQL Server 啟動時連線失敗` +
                    `（${status.sqliteFallbackReason || '原因不明'}），資料暫時寫入本機資料庫。` +
                    `修好 SQL Server 後重新啟動即可切回，這段期間的資料不會自動搬過去。`;
                banner.classList.remove('d-none');
            }
        } catch {
            // 提醒層失敗不影響聊天功能
        }
    }

    document.addEventListener('DOMContentLoaded', init);
})();
