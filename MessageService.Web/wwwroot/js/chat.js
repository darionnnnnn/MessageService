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
        searchRequestToken: 0
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

    // 跳轉到搜尋結果後，把目前渲染出來的訊息串裡符合關鍵字的文字節點換成上面那份高亮結果
    // （只處理純文字節點、跳過 <a> 連結內部，避免弄壞連結）；每個文字節點只標第一個符合的地方，
    // 對聊天訊息這種短文字已經夠用，不做進一步的多重比對
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
                textNodes.push(node);
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

        if (message.content && isDownloadInProgress(message.content.downloadStatus)) {
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
        if (group.unreadCount > 0 && group.groupId !== state.groupId) {
            const badge = document.createElement('span');
            badge.className = 'group-item-badge';
            badge.textContent = group.unreadCount > 99 ? '99+' : String(group.unreadCount);
            meta.appendChild(badge);
        }

        btn.appendChild(meta);

        btn.addEventListener('click', () => {
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
            return;
        }

        await selectGroup(groups[0].groupId);
    }

    // loadGroups() 只在頁面載入時跑一次；之後靠這支輪詢讓側欄（新群組/預覽/排序）
    // 定期跟資料庫同步，不然使用者在別的群組發言，側欄要重新整理才會出現
    async function pollGroups() {
        if (document.hidden || state.groupsPolling) {
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

            if (state.groupId === null && groups.length > 0) {
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
    const FONT_BASE_PX_MIN = 12;
    const FONT_BASE_PX_MAX = 28;

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

        // 設定 modal 關掉時，如果這次開啟期間真的改了東西（名稱顯示模式、關鍵字規則等），
        // settings.js 會發這個事件——重新載入目前群組的訊息視窗＋側欄，不用使用者自己重新整理
        document.addEventListener('messageservice:settings-changed', () => {
            if (state.groupId) {
                selectGroup(state.groupId);
            }
            pollGroups();
        });

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
