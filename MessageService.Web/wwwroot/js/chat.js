(() => {
    'use strict';

    const POLL_INTERVAL_MS = 4000;
    const NEAR_BOTTOM_THRESHOLD_PX = 80;
    const INITIAL_DAYS = 3;
    const LOAD_MORE_DAYS = 7;
    // 與 MessagesController.MaxDays 對齊；超過就別再放大視窗，免得按鈕變成按了沒反應
    const MAX_DAYS_WINDOW = 3650;
    const AVATAR_COLORS = ['#f28b82', '#fbbc04', '#34a853', '#4285f4', '#a142f4', '#ff6d01', '#00acc1', '#c2185b'];
    const GROUP_AVATAR_COLOR = '#9AACC2';
    const FONT_SIZE_STORAGE_KEY = 'chat-font-size';
    const FONT_SIZES = ['small', 'medium', 'large'];
    const DEFAULT_FONT_SIZE = 'medium';
    const PREVIEW_URL_REGEX = /(https?:\/\/[^\s]+)/g;

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
        daysWindow: INITIAL_DAYS,
        hasMoreOlder: false,
        loadingOlder: false,
        polling: false,
        // 每次切換群組就 +1；非同步請求回來時若對不上，代表是前一個群組的過期回應，必須丟棄
        requestToken: 0,
        following: true,
        unreadCount: 0,
        pendingContentIds: new Set(),
        lastAppendedDateKey: null,
        lastAppendedSenderId: null,
        connectionOk: true
    };

    const els = {};

    function $(id) {
        return document.getElementById(id);
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

    function createDateSeparator(iso) {
        const sep = document.createElement('div');
        sep.className = 'date-separator';
        const span = document.createElement('span');
        span.textContent = formatDateSeparator(iso);
        sep.appendChild(span);
        return sep;
    }

    // === 訊息內容渲染 ===

    // 純文字訊息把網址轉成可點連結，其餘一律當純文字節點，不解析 HTML（訊息內容是外部輸入）
    function appendLinkifiedText(container, text) {
        let lastIndex = 0;
        for (const match of text.matchAll(PREVIEW_URL_REGEX)) {
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

    function buildReadyContentNode(messageType, contentId, fileName) {
        const url = `/api/messages/${contentId}/content`;

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

    function buildContentNode(message) {
        const type = message.messageType;

        if (type === 'text') {
            const div = document.createElement('div');
            appendLinkifiedText(div, message.text ?? '');
            return div;
        }
        if (type === 'sticker') {
            const div = document.createElement('div');
            div.textContent = message.text ?? '(貼圖)';
            return div;
        }

        const content = message.content;
        if (!content) {
            const div = document.createElement('div');
            div.textContent = message.text ?? '';
            return div;
        }

        if (content.downloadStatus === 'Pending') {
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

        const bubble = document.createElement('div');
        bubble.className = 'bubble'
            + (message.messageType === 'sticker' ? ' sticker' : '')
            + (showAvatarAndName ? ' has-tail' : '');
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

        if (message.content && message.content.downloadStatus === 'Pending') {
            state.pendingContentIds.add(message.content.id);
        }

        return row;
    }

    function appendMessages(messages, animate) {
        const list = els.messageList;
        for (const message of messages) {
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
        const previousScrollHeight = list.scrollHeight;
        const previousScrollTop = list.scrollTop;

        const fragment = document.createDocumentFragment();
        let lastDateKey = null;
        let lastSenderId = null;
        for (const message of messages) {
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
        list.scrollTop = previousScrollTop + (list.scrollHeight - previousScrollHeight);
    }

    function updateContentNode(status) {
        const pendingEl = els.messageList.querySelector(`.msg-pending[data-content-id="${status.contentId}"]`);
        if (!pendingEl) {
            return;
        }
        const replacement = status.downloadStatus === 'Completed'
            ? buildReadyContentNode(pendingEl.dataset.messageType, status.contentId, pendingEl.dataset.fileName)
            : buildFailedNode();
        pendingEl.replaceWith(replacement);
    }

    function openImageModal(url) {
        els.imageModalImg.src = url;
        bootstrap.Modal.getOrCreateInstance(els.imageModal).show();
    }

    // === 置底跟隨 ===

    function isNearBottom() {
        const list = els.messageList;
        return list.scrollHeight - list.scrollTop - list.clientHeight < NEAR_BOTTOM_THRESHOLD_PX;
    }

    function scrollToBottom(smooth) {
        els.messageList.scrollTo({ top: els.messageList.scrollHeight, behavior: smooth ? 'smooth' : 'auto' });
    }

    function updateFollowUi() {
        els.scrollBottomBtn.classList.toggle('d-none', state.following);
        els.unreadBadge.classList.toggle('d-none', state.unreadCount === 0);
        els.unreadBadge.textContent = String(state.unreadCount);
    }

    function setFollowing(following) {
        state.following = following;
        if (following) {
            state.unreadCount = 0;
        }
        updateFollowUi();
    }

    // === 側欄：群組列表 ===

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
            return;
        }

        if (groups.length === 0) {
            const empty = document.createElement('div');
            empty.className = 'group-list-empty';
            empty.textContent = '找不到符合的群組';
            els.groupList.appendChild(empty);
            return;
        }

        for (const group of groups) {
            els.groupList.appendChild(createGroupItem(group));
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

        if (group.lastMessageAt) {
            const time = document.createElement('div');
            time.className = 'group-item-time';
            time.textContent = formatTime(group.lastMessageAt);
            btn.appendChild(time);
        }

        btn.addEventListener('click', () => {
            selectGroup(group.groupId);
            els.chatApp.classList.add('mobile-chat-open');
        });

        return btn;
    }

    function updateActiveGroupItem() {
        for (const item of els.groupList.querySelectorAll('.group-item')) {
            item.classList.toggle('active', item.dataset.groupId === state.groupId);
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

    // === 資料載入 ===

    async function loadGroups() {
        const groups = await fetchJson('/api/groups');
        state.groups = groups;
        renderGroupList(els.groupSearch.value);

        if (groups.length === 0) {
            return;
        }

        await selectGroup(groups[0].groupId);
    }

    async function selectGroup(groupId) {
        const token = ++state.requestToken;

        state.groupId = groupId;
        state.oldestId = null;
        state.newestId = null;
        state.daysWindow = INITIAL_DAYS;
        state.hasMoreOlder = false;
        setFollowing(true);
        clearMessageList();

        updateActiveGroupItem();
        updateChatHeader(state.groups.find(g => g.groupId === groupId));
        updateLoadMoreButton();

        try {
            const page = await fetchJson(
                `/api/groups/${encodeURIComponent(groupId)}/messages?days=${state.daysWindow}`);
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
        state.pendingContentIds.clear();
        state.lastAppendedDateKey = null;
        state.lastAppendedSenderId = null;
    }

    /// 用一次完整的天數視窗查詢結果重繪整個清單（初次載入、以及沒有游標可用時的「載入更早」都走這裡）
    function renderWindow(page) {
        clearMessageList();
        appendMessages(page.messages, false);
        if (page.messages.length > 0) {
            state.oldestId = page.messages[0].id;
        }
        state.newestId = page.latestId ?? state.newestId;
        state.hasMoreOlder = page.hasMore;
        updateLoadMoreButton();
        scrollToBottom(false);
    }

    function updateLoadMoreButton() {
        els.loadMoreBtn.disabled = !state.hasMoreOlder;
        els.loadMoreBtn.textContent = state.hasMoreOlder ? '載入更早 7 天' : '沒有更早的訊息';
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
                ? `/api/groups/${encodeURIComponent(state.groupId)}/messages?days=${state.daysWindow + LOAD_MORE_DAYS}`
                : `/api/groups/${encodeURIComponent(state.groupId)}/messages?beforeId=${state.oldestId}&days=${LOAD_MORE_DAYS}`;
            const page = await fetchJson(url);
            if (token !== state.requestToken) {
                return;
            }

            if (growWindow) {
                state.daysWindow += LOAD_MORE_DAYS;
                renderWindow(page);
            } else {
                if (page.messages.length > 0) {
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

    async function pollNewer() {
        // 連線慢時輪詢可能比間隔還久，沒有這個旗標的話兩輪會讀到同一個 newestId 而把訊息插兩次
        if (!state.groupId || document.hidden || state.polling || state.loadingOlder) {
            return;
        }
        const token = state.requestToken;
        state.polling = true;
        try {
            if (state.newestId != null) {
                const page = await fetchJson(
                    `/api/groups/${encodeURIComponent(state.groupId)}/messages?afterId=${state.newestId}`);
                if (token !== state.requestToken) {
                    return;
                }
                if (page.messages.length > 0) {
                    const wasFollowing = state.following;
                    appendMessages(page.messages, true);
                    state.newestId = page.messages[page.messages.length - 1].id;
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
        const ids = Array.from(state.pendingContentIds).join(',');
        const statuses = await fetchJson(`/api/messages/statuses?ids=${ids}`);
        if (token !== state.requestToken) {
            return;
        }
        for (const status of statuses) {
            if (status.downloadStatus !== 'Pending') {
                state.pendingContentIds.delete(status.contentId);
                updateContentNode(status);
            }
        }
    }

    // === 字體大小（存 localStorage，每台裝置各自記，不進 DB） ===

    function applyFontSize(size) {
        for (const s of FONT_SIZES) {
            els.chatApp.classList.toggle(`font-size-${s}`, s === size);
        }
        for (const btn of els.fontSizeButtons) {
            btn.classList.toggle('active', btn.dataset.fontSize === size);
        }
    }

    function initFontSizeToggle() {
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

    // === 初始化 ===

    function init() {
        els.chatApp = $('chat-app');
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
        els.messageList = $('message-list');
        els.scrollBottomBtn = $('scroll-bottom-btn');
        els.unreadBadge = $('unread-badge');
        els.imageModal = $('image-modal');
        els.imageModalImg = $('image-modal-img');
        els.fontSizeButtons = Array.from(document.querySelectorAll('.font-size-toggle [data-font-size]'));

        initFontSizeToggle();

        els.loadMoreBtn.addEventListener('click', loadOlder);
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
        });

        loadGroups().catch(() => setConnectionOk(false));
        setInterval(pollNewer, POLL_INTERVAL_MS);
    }

    document.addEventListener('DOMContentLoaded', init);
})();
