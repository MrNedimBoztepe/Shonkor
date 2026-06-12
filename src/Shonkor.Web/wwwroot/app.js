// Shonkor Dashboard Frontend Application Logic
// Uses vis.js for force-directed graph rendering and Prism.js for syntax highlighting

document.addEventListener('DOMContentLoaded', () => {
    // Initialize Lucide Icons
    lucide.createIcons();

    // ============================
    // i18n Localization Engine
    // ============================
    const langDropdownBtn = document.getElementById('lang-dropdown-btn');
    const langOptions = document.getElementById('lang-options');
    const currentLangFlag = document.getElementById('current-lang-flag');
    const currentLangText = document.getElementById('current-lang-text');

    if (langDropdownBtn && langOptions) {
        const savedLang = localStorage.getItem('shonkor_lang') || 'en';
        updateLangUI(savedLang);
        loadLanguage(savedLang);

        langDropdownBtn.addEventListener('click', (e) => {
            e.stopPropagation();
            const open = langOptions.classList.toggle('hidden') === false;
            langDropdownBtn.setAttribute('aria-expanded', String(open));
        });

        langOptions.querySelectorAll('li').forEach(li => {
            // Keyboard-accessible: each option is focusable and activatable via Enter/Space.
            li.setAttribute('tabindex', '0');
            li.setAttribute('role', 'option');
            const choose = () => {
                const lang = li.getAttribute('data-value');
                localStorage.setItem('shonkor_lang', lang);
                updateLangUI(lang);
                loadLanguage(lang);
                langOptions.classList.add('hidden');
                langDropdownBtn.setAttribute('aria-expanded', 'false');
            };
            li.addEventListener('click', choose);
            li.addEventListener('keydown', (e) => {
                if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); choose(); }
            });
        });

        document.addEventListener('click', (e) => {
            if (!langDropdownBtn.contains(e.target) && !langOptions.contains(e.target)) {
                langOptions.classList.add('hidden');
                langDropdownBtn.setAttribute('aria-expanded', 'false');
            }
        });
    }

    function updateLangUI(lang) {
        const flagMap = { 'en': 'gb', 'de': 'de', 'tr': 'tr' };
        const nameMap = { 'en': 'English', 'de': 'Deutsch', 'tr': 'Türkçe' };
        if (currentLangFlag && currentLangText) {
            currentLangFlag.src = `https://flagcdn.com/w20/${flagMap[lang] || 'gb'}.png`;
            currentLangText.textContent = nameMap[lang] || lang.toUpperCase();
        }
    }

    let currentTranslations = {};
    window.t = function(key) { return currentTranslations[key] || key; };

    async function loadLanguage(lang) {
        try {
            const res = await fetch(`/i18n/lang_${lang}.po?v=2.0.13`);
            if (res.ok) {
                const text = await res.text();
                currentTranslations = parsePO(text);
                applyTranslations();
            }
        } catch (e) {
            console.error("Failed to load language file:", e);
        }
    }

    function parsePO(content) {
        const translations = {};
        const regex = /msgid\s+"([^"]+)"\s*\n\s*msgstr\s+"([^"]+)"/g;
        let match;
        while ((match = regex.exec(content.replace(/\r/g, ''))) !== null) {
            translations[match[1]] = match[2];
        }
        return translations;
    }

    function applyTranslations() {
        document.querySelectorAll('[data-i18n]').forEach(el => {
            const key = el.getAttribute('data-i18n');
            if (currentTranslations[key]) {
                setTranslatedText(el, currentTranslations[key]);
            }
        });
        document.querySelectorAll('[data-i18n-placeholder]').forEach(el => {
            const key = el.getAttribute('data-i18n-placeholder');
            if (currentTranslations[key]) {
                el.placeholder = currentTranslations[key];
            }
        });
    }

    // Sets translated text WITHOUT using innerHTML: avoids an XSS sink and, crucially,
    // preserves any child elements such as Lucide icons inside the translated element.
    function setTranslatedText(el, text) {
        const hasElementChildren = Array.from(el.childNodes).some(n => n.nodeType === Node.ELEMENT_NODE);
        if (!hasElementChildren) {
            el.textContent = text;
            return;
        }
        // Element contains icons/markup: only update the (first non-empty) text node.
        const textNode = Array.from(el.childNodes).find(n => n.nodeType === Node.TEXT_NODE && n.textContent.trim().length > 0);
        if (textNode) {
            // Keep a leading space so the label stays separated from a preceding icon.
            textNode.textContent = ' ' + text;
        } else {
            el.appendChild(document.createTextNode(' ' + text));
        }
    }

    // DOM Elements
    const statsNodesEl = document.getElementById('stats-nodes');
    const statsEdgesEl = document.getElementById('stats-edges');
    const globalSearchInput = document.getElementById('global-search');
    const searchBtn = document.getElementById('search-btn');
    const resultsCountEl = document.getElementById('results-count');
    const resultsListEl = document.getElementById('search-results-list');
    const triggerScanBtn = document.getElementById('trigger-scan-btn');
    const themeToggleBtn = document.getElementById('theme-toggle-btn');
    const themeIcon = document.getElementById('theme-icon');

    // Theme Toggle Logic
    let isLightMode = window.matchMedia('(prefers-color-scheme: light)').matches;
    if (localStorage.getItem('shonkor-theme') === 'light') isLightMode = true;
    if (localStorage.getItem('shonkor-theme') === 'dark') isLightMode = false;

    function applyTheme() {
        if (isLightMode) {
            document.documentElement.classList.add('theme-light');
            document.documentElement.classList.remove('theme-dark');
        } else {
            document.documentElement.classList.add('theme-dark');
            document.documentElement.classList.remove('theme-light');
        }
        
        if (themeToggleBtn) {
            themeToggleBtn.innerHTML = `<i data-lucide="${isLightMode ? 'sun' : 'moon'}" id="theme-icon"></i>`;
        }
        
        if (window.lucide) window.lucide.createIcons();
    }
    applyTheme();

    if (themeToggleBtn) {
        themeToggleBtn.addEventListener('click', () => {
            isLightMode = !isLightMode;
            localStorage.setItem('shonkor-theme', isLightMode ? 'light' : 'dark');
            applyTheme();
        });
    }
    
    // Capsule synthesis DOM
    const capsuleQueryInput = document.getElementById('capsule-query');
    const typeFilter = document.getElementById('type-filter');
    const askAiBtn = document.getElementById('ask-ai-btn');
    const ragAnswerOverlay = document.getElementById('rag-answer-overlay');
    const ragAnswerContent = document.getElementById('rag-answer-content');
    const closeRagBtn = document.getElementById('close-rag-btn');
    const capsuleHopsSelect = document.getElementById('capsule-hops');
    const generateCapsuleBtn = document.getElementById('generate-capsule-btn');
    
    // Layout Dropdown
    const cycleLayoutBtn = null; // Removed in favor of select
    
    // Graph control DOM
    const fitGraphBtn = document.getElementById('fit-graph-btn');
    const clearGraphBtn = document.getElementById('clear-graph-btn');
    
    // Drawer DOM
    const detailsDrawer = document.getElementById('details-drawer');
    const drawerNodeType = document.getElementById('drawer-node-type');
    const drawerNodeName = document.getElementById('drawer-node-name');
    const drawerNodePath = document.getElementById('drawer-node-path');
    const drawerNodeProperties = document.getElementById('drawer-node-properties');
    const drawerNodeRelations = document.getElementById('drawer-node-relations');
    const drawerNodeCode = document.getElementById('drawer-node-code');
    const closeDrawerBtn = document.getElementById('close-drawer-btn');
    
    // Modal DOM
    const capsuleModal = document.getElementById('capsule-modal');
    const closeModalBtn = document.getElementById('close-modal-btn');
    const modalNodesCount = document.getElementById('modal-nodes-count');
    const modalEdgesCount = document.getElementById('modal-edges-count');
    const copyCapsuleBtn = document.getElementById('copy-capsule-btn');
    const capsuleText = document.getElementById('capsule-text');

    if (closeRagBtn) {
        closeRagBtn.addEventListener('click', () => {
            ragAnswerOverlay.classList.add('hidden');
        });
    }

    // ===== AI Chat panel (opens over the graph; graph hidden behind) =====
    const aiChatPanel = document.getElementById('ai-chat-panel');
    const aiChatMessages = document.getElementById('ai-chat-messages');
    const aiChatForm = document.getElementById('ai-chat-form');
    const aiChatInput = document.getElementById('ai-chat-input');
    const aiChatContext = document.getElementById('ai-chat-context');
    const closeAiChatBtn = document.getElementById('close-ai-chat-btn');

    // Node IDs (top search hits) used as RAG context for the whole chat session.
    let aiChatContextIds = [];
    // Running conversation transcript so follow-up questions have prior context.
    let aiChatHistory = [];
    // Signature of the context nodes the current conversation was started with; used to detect
    // when a new search changed the context so the chat can reset instead of mixing sessions.
    let aiChatSessionKey = null;

    function aiChatScrollToBottom() {
        if (aiChatMessages) aiChatMessages.scrollTop = aiChatMessages.scrollHeight;
    }

    function aiAppendMessage(role, html, isHtml = true) {
        const el = document.createElement('div');
        el.className = `ai-msg ${role}`;
        if (isHtml) el.innerHTML = html; else el.textContent = html;
        aiChatMessages.appendChild(el);
        aiChatScrollToBottom();
        return el;
    }

    // Render model/markdown output safely: sanitize (DOMPurify) before insertion so an answer that
    // echoes raw HTML (e.g. from indexed file content) can't inject script. Falls back to plain text.
    function renderMarkdownSafe(md, el) {
        if (window.marked && window.DOMPurify) {
            el.innerHTML = window.DOMPurify.sanitize(window.marked.parse(md));
        } else {
            el.textContent = md;
        }
    }

    function openAiChat() {
        if (!aiChatPanel) return;

        // Capture the current search hits as the context for this chat session.
        aiChatContextIds = (lastSearchResults || []).slice(0, 10)
            .map(res => (res.Node || res.node)?.Id)
            .filter(Boolean);

        // If the context changed since the conversation started, reset the chat — otherwise a new
        // session would carry over the old messages/transcript and ask them against the new nodes.
        const sessionKey = aiChatContextIds.join('|');
        if (sessionKey !== aiChatSessionKey) {
            aiChatSessionKey = sessionKey;
            aiChatHistory = [];
            if (aiChatMessages) aiChatMessages.innerHTML = '';
        }

        if (aiChatContext) {
            aiChatContext.textContent = aiChatContextIds.length
                ? `${aiChatContextIds.length} ${window.t ? window.t('nodes') : 'nodes'}`
                : '';
        }

        // Greeting / empty-state when there is no conversation yet.
        if (aiChatMessages && aiChatMessages.childElementCount === 0) {
            const hint = aiChatContextIds.length
                ? 'Ask me anything about the nodes from your current search.'
                : 'Run a search first so I have nodes to reason about, then ask your question here.';
            aiChatMessages.innerHTML =
                `<div class="ai-chat-empty"><i data-lucide="sparkles"></i><p>${hint}</p></div>`;
            if (window.lucide) window.lucide.createIcons();
        }

        aiChatPanel.classList.remove('hidden');
        setTimeout(() => aiChatInput && aiChatInput.focus(), 60);
    }

    function closeAiChat() {
        if (aiChatPanel) aiChatPanel.classList.add('hidden');
    }

    async function sendAiChatQuestion() {
        const question = (aiChatInput.value || '').trim();
        if (!question) return;

        // Clear the empty-state greeting on first real message.
        const empty = aiChatMessages.querySelector('.ai-chat-empty');
        if (empty) empty.remove();

        aiAppendMessage('user', escapeHtml(question), true);
        aiChatInput.value = '';
        aiChatInput.style.height = 'auto';

        if (aiChatContextIds.length === 0) {
            aiAppendMessage('error', 'No context nodes available. Please run a search first.', false);
            return;
        }

        // Record the turn and build a transcript-aware query (last few turns) so the
        // model can answer follow-ups in context. The endpoint is stateless, so we pass
        // the short transcript as the query alongside the same context nodes.
        aiChatHistory.push({ role: 'user', text: question });
        const recent = aiChatHistory.slice(-6);
        const composedQuery = recent.length > 1
            ? recent.map(m => `${m.role === 'user' ? 'User' : 'Assistant'}: ${m.text}`).join('\n')
            : question;

        const thinking = aiAppendMessage('assistant',
            `<div style="display:flex; align-items:center; gap:0.5rem;"><div class="spinner" style="width:16px; height:16px; border-width:2px;"></div><span>Thinking…</span></div>`, true);

        try {
            const res = await fetch('/api/ask', {
                method: 'POST',
                headers: {
                    'Content-Type': 'application/json',
                    'X-Project-Name': activeProjectName.textContent
                },
                body: JSON.stringify({ query: composedQuery, nodeIds: aiChatContextIds })
            });

            if (res.ok) {
                const data = await res.json();
                const answer = data.response || 'No answer.';
                aiChatHistory.push({ role: 'assistant', text: answer });
                renderMarkdownSafe(answer, thinking);
            } else {
                // Most failures here are the AI backend (Ollama) being unreachable.
                thinking.className = 'ai-msg error';
                thinking.textContent = res.status >= 500
                    ? "Couldn't reach the AI backend. Make sure Ollama is running with the configured model, then try again."
                    : `Error: ${await res.text()}`;
            }
        } catch (err) {
            console.error('Ask AI Error:', err);
            thinking.className = 'ai-msg error';
            thinking.textContent = "Couldn't reach the AI backend. Make sure Ollama is running, then try again.";
        }
        aiChatScrollToBottom();
    }

    if (askAiBtn) {
        askAiBtn.addEventListener('click', openAiChat);
    }
    if (closeAiChatBtn) {
        closeAiChatBtn.addEventListener('click', closeAiChat);
    }
    if (aiChatForm) {
        aiChatForm.addEventListener('submit', (e) => { e.preventDefault(); sendAiChatQuestion(); });
    }
    if (aiChatInput) {
        // Enter sends, Shift+Enter inserts a newline; textarea auto-grows.
        aiChatInput.addEventListener('keydown', (e) => {
            if (e.key === 'Enter' && !e.shiftKey) { e.preventDefault(); sendAiChatQuestion(); }
        });
        aiChatInput.addEventListener('input', () => {
            aiChatInput.style.height = 'auto';
            aiChatInput.style.height = Math.min(aiChatInput.scrollHeight, 140) + 'px';
        });
    }

    // Project Manager DOM
    const activeProjectBtn = document.getElementById('active-project-btn');
    const activeProjectName = document.getElementById('active-project-name');
    const projectDropdown = document.getElementById('project-dropdown');
    const dropdownSearchInput = document.getElementById('dropdown-search-input');
    const dropdownProjectList = document.getElementById('dropdown-project-list');
    const openSettingsBtn = document.getElementById('open-settings-btn');
    const openAdminBtn = document.getElementById('open-admin-btn');
    const projectModal = document.getElementById('project-modal');
    const adminModal = document.getElementById('admin-modal');
    const closeProjectModalBtn = document.getElementById('close-project-modal-btn');
    const closeAdminModalBtn = document.getElementById('close-admin-modal-btn');
    const projectListEl = document.getElementById('project-list');
    
    // New project registration inputs
    const newProjectNameInput = document.getElementById('new-project-name');
    const newProjectPathInput = document.getElementById('new-project-path');
    const addProjectBtn = document.getElementById('add-project-btn');
    
    // Active project config form inputs
    const configTitleEl = document.getElementById('config-title');
    const configDbPathInput = document.getElementById('config-db-path');
    const configExclusionsTextarea = document.getElementById('config-exclusions');
    const saveConfigBtn = document.getElementById('save-config-btn');
    const scanProjectBtn = document.getElementById('scan-project-btn');
    const deleteProjectBtn = document.getElementById('delete-project-btn');
    const activateSelectedProjectBtn = document.getElementById('activate-selected-project-btn');
    
    // Folder Browser DOM
    const browseFolderBtn = document.getElementById('browse-folder-btn');
    const browseModal = document.getElementById('browse-modal');
    const closeBrowseModalBtn = document.getElementById('close-browse-modal-btn');
    const browseUpBtn = document.getElementById('browse-up-btn');
    const browseCurrentPathInput = document.getElementById('browse-current-path');
    const browseGoBtn = document.getElementById('browse-go-btn');
    const browseDrivesList = document.getElementById('browse-drives-list');
    const browseFoldersList = document.getElementById('browse-folders-list');
    const browseSelectedPathLabel = document.getElementById('browse-selected-path-label');
    const browseCancelBtn = document.getElementById('browse-cancel-btn');
    const browseConfirmBtn = document.getElementById('browse-confirm-btn');
    
    // Plugin Management DOM
    const pluginListEl = document.getElementById('plugin-list');
    const pluginEmptyState = document.getElementById('plugin-list-empty');
    const pluginNameInput = document.getElementById('plugin-name');
    const pluginExtensionInput = document.getElementById('plugin-extension');
    const createPluginBtn = document.getElementById('create-plugin-btn');

    // Toast DOM
    const toast = document.getElementById('toast');
    const toastMessage = document.getElementById('toast-message');

    // Force-Graph WebGL Variables
    let network = null; // This will hold the ForceGraph instance
    let graphData = { nodes: [], links: [] };
    let allDiscoveredNodes = new Map(); // Keep track of node definitions by ID
    let currentSelectedNodeId = null;

    // Folder Browser State Variables
    let browseCurrentPath = '';
    let browseSelectedPath = '';

    // Project Manager State Variables
    let registeredProjects = [];
    let activeProject = "";
    let selectedConfigProject = null;
    let projectFilter = 'all'; // 'all' or 'active'
    let projectSearchQuery = '';

    // Graph Interaction & Filter State Variables
    let allKnownTypes = []; // Array of { typeName, category, isVisibleByDefault }
    let databaseNodeTypes = new Set(); // Types actually present in the project DB
    let activeTypes = new Set();
    let lastSearchResults = [];
    let dragSelectMode = false;
    let physicsEnabled = true; // Physics ON by default as requested

    // Golden Ratio for evenly distributing clusters around the center
    const GOLDEN_RATIO = 0.618033988749895;
    const typeAngles = new Map();
    let typeIndex = 0;

    function getClusterCoords(type) {
        if (!typeAngles.has(type)) {
            typeAngles.set(type, typeIndex++);
        }
        const i = typeAngles.get(type);
        const angle = i * GOLDEN_RATIO * Math.PI * 2;
        const distance = 800 + Math.random() * 400; // Ring distance
        const offsetX = (Math.random() - 0.5) * 500; // Spread within cluster
        const offsetY = (Math.random() - 0.5) * 500;
        
        return {
            x: Math.cos(angle) * distance + offsetX,
            y: Math.sin(angle) * distance + offsetY
        };
    }

    const layoutModes = [
        { id: 'physics', name: 'Layout: Physics', physics: true, dagMode: null },
        { id: 'semantic', name: 'Layout: Semantic', physics: false, dagMode: null },
        { id: 'tree', name: 'Layout: Tree', physics: true, dagMode: 'td' },
        { id: 'radial', name: 'Layout: Radial', physics: true, dagMode: 'radialout' }
    ];

    // Helper to get nodes matching active filters
    function getFilteredNodes() {
        return graphData.nodes.filter(node => activeTypes.has(node.type) || activeTypes.has(node.Type));
    }

    // Global fetch interceptor to automatically attach active project header
    const originalFetch = window.fetch.bind(window);
    window.fetch = function (url, options = {}) {
        if (typeof url === 'string' && url.startsWith('/api/') && !url.startsWith('/api/projects')) {
            options.headers = options.headers || {};
            if (activeProject) {
                if (options.headers instanceof Headers) {
                    options.headers.set('X-Project-Name', activeProject);
                } else if (Array.isArray(options.headers)) {
                    options.headers.push(['X-Project-Name', activeProject]);
                } else {
                    options.headers['X-Project-Name'] = activeProject;
                }
            }
            if (!options.cache) {
                options.cache = 'no-store';
            }
        }
        return originalFetch(url, options);
    };

    // Utility for updating loading state steps
    function updateLoadingState(text, show) {
        const loadingOverlay = document.getElementById('graph-loading-overlay');
        const loadingText = document.getElementById('graph-loading-text');
        
        if (loadingOverlay) {
            if (show) loadingOverlay.classList.remove('hidden');
            else loadingOverlay.classList.add('hidden');
        }
        if (loadingText && text) {
            loadingText.textContent = text;
        }
    }

    // Normalization translation layer to handle camelCase JSON vs PascalCase backend models
    function normalizeNode(node) {
        if (!node) return null;
        node.id = node.Id = node.Id || node.id || "";
        node.name = node.Name = node.Name || node.name || "";
        node.type = node.Type = node.Type || node.type || "";
        node.filePath = node.FilePath = node.FilePath || node.filePath || "";
        node.properties = node.Properties = node.Properties || node.properties || {};
        
        const props = node.properties;
        if (props) {
            props.content = props.Content = props.Content || props.content || "";
            props.filePath = props.FilePath = props.FilePath || props.filePath || "";
            props.startLine = props.StartLine = props.StartLine || props.startLine || null;
            props.endLine = props.EndLine = props.EndLine || props.endLine || null;
            props.contentHash = props.ContentHash = props.ContentHash || props.contentHash || null;
        }
        
        return node;
    }

    function normalizeEdge(edge) {
        if (!edge) return null;
        edge.sourceId = edge.SourceId = edge.SourceId || edge.sourceId || "";
        edge.targetId = edge.TargetId = edge.TargetId || edge.targetId || "";
        edge.relationship = edge.Relationship = edge.Relationship || edge.relationship || edge.relationType || edge.RelationType || "";
        return edge;
    }

    function normalizeResult(res) {
        if (!res) return null;
        res.node = res.Node = normalizeNode(res.node || res.Node);
        res.score = res.Score = res.score !== undefined ? res.score : res.Score;
        res.relatedEdges = res.RelatedEdges = (res.relatedEdges || res.RelatedEdges || []).map(normalizeEdge);
        return res;
    }

    // Initialize Network Graph Viewport
    initNetwork();

    // Load initial projects state on startup
    initProjects();
    
    // Load Dashboard
    loadDashboard();

    const dashboardOverlay = document.getElementById('dashboard-overlay');
    const viewGraphBtn = document.getElementById('view-graph-btn');
    const viewDashboardBtn = document.getElementById('view-dashboard-btn');
    const closeDashboardBtn = document.getElementById('close-dashboard-btn');
    
    // Add hidden class initially if not there
    if (dashboardOverlay && !dashboardOverlay.classList.contains('hidden')) {
        dashboardOverlay.classList.add('hidden');
    }

    if (viewGraphBtn && viewDashboardBtn) {
        viewGraphBtn.addEventListener('click', () => {
            viewGraphBtn.classList.add('active');
            viewDashboardBtn.classList.remove('active');
            hideDashboard();
        });

        viewDashboardBtn.addEventListener('click', () => {
            viewDashboardBtn.classList.add('active');
            viewGraphBtn.classList.remove('active');
            showDashboard();
            loadDashboard();
        });
        
        if (closeDashboardBtn) {
            closeDashboardBtn.addEventListener('click', () => {
                if (viewGraphBtn) viewGraphBtn.classList.add('active');
                if (viewDashboardBtn) viewDashboardBtn.classList.remove('active');
                hideDashboard();
            });
        }
    }

    function showDashboard() {
        if (dashboardOverlay) dashboardOverlay.classList.remove('hidden');
    }
    function hideDashboard() {
        if (dashboardOverlay) dashboardOverlay.classList.add('hidden');
    }

    function escapeHtml(unsafe) {
        if (!unsafe) return '';
        return unsafe
            .replace(/&/g, "&amp;")
            .replace(/</g, "&lt;")
            .replace(/>/g, "&gt;")
            .replace(/"/g, "&quot;")
            .replace(/'/g, "&#039;");
    }

    async function loadDashboard() {
        try {
            const res = await fetch('/api/interactions', {
                headers: { 'X-Project-Name': activeProjectName.textContent }
            });
            if (res.ok) {
                const items = await res.json();
                renderKanban(items);
            } else {
                showToast('Could not load the dashboard interactions.');
            }
        } catch (e) {
            console.error("Failed to load dashboard:", e);
            showToast('Network error while loading the dashboard.');
        }
    }

    function renderKanban(items) {
        const qCol = document.querySelector('#kanban-questions .kanban-items');
        const tCol = document.querySelector('#kanban-tasks .kanban-items');
        const dCol = document.querySelector('#kanban-decisions .kanban-items');
        const mCol = document.querySelector('#kanban-milestones .kanban-items');
        
        if (!qCol || !tCol || !dCol || !mCol) return;

        qCol.innerHTML = '';
        tCol.innerHTML = '';
        dCol.innerHTML = '';
        mCol.innerHTML = '';

        items.forEach(item => {
            const card = document.createElement('div');
            card.className = 'kanban-card';

            const status = (item.properties && item.properties.status) ? item.properties.status : 'Open';

            // Status options per interaction type (the current one is highlighted; the others act as set-buttons).
            const STATUS_OPTIONS = {
                Question: ['Open', 'Resolved'],
                Task: ['Todo', 'In Progress', 'Done'],
                Decision: ['Proposed', 'Accepted', 'Superseded'],
                Milestone: ['In Progress', 'Completed', 'Blocked']
            };
            const options = STATUS_OPTIONS[item.type] || [status];

            const title = document.createElement('div');
            title.className = 'kanban-card-title';
            title.textContent = item.name || 'Untitled';

            const desc = document.createElement('div');
            desc.className = 'kanban-card-desc';
            desc.textContent = item.content || '';

            const actions = document.createElement('div');
            actions.className = 'kanban-card-status';
            options.forEach(opt => {
                const btn = document.createElement('button');
                btn.type = 'button';
                btn.className = 'kanban-status-btn' + (opt === status ? ' active' : '');
                btn.textContent = opt;
                btn.setAttribute('aria-pressed', String(opt === status));
                btn.setAttribute('aria-label', `Set status of "${item.name || 'item'}" to ${opt}`);
                btn.addEventListener('click', (e) => {
                    e.stopPropagation();
                    if (opt !== status) updateInteractionStatus(item.id || item.Id, opt);
                });
                actions.appendChild(btn);
            });

            card.appendChild(title);
            if (item.content) card.appendChild(desc);
            card.appendChild(actions);

            // Clicking the card body (not a status button) finds it in the graph.
            card.addEventListener('click', () => {
                hideDashboard();
                globalSearchInput.value = item.name;
                performSearch();
            });

            if (item.type === 'Question') qCol.appendChild(card);
            else if (item.type === 'Task') tCol.appendChild(card);
            else if (item.type === 'Decision') dCol.appendChild(card);
            else if (item.type === 'Milestone') mCol.appendChild(card);
        });

        // Per-column empty states so a board with no items isn't four blank columns.
        const emptyHints = [
            [qCol, 'No open questions yet.'],
            [tCol, 'No tasks recorded yet.'],
            [dCol, 'No decisions logged yet.'],
            [mCol, 'No milestones yet.']
        ];
        for (const [col, hint] of emptyHints) {
            if (col.childElementCount === 0) {
                const empty = document.createElement('div');
                empty.className = 'kanban-empty';
                empty.textContent = hint;
                col.appendChild(empty);
            }
        }
    }

    // Persist a new status for an interaction node, then refresh the board.
    async function updateInteractionStatus(id, status) {
        if (!id) return;
        try {
            const res = await fetch('/api/interactions/status', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json', 'X-Project-Name': activeProjectName.textContent },
                body: JSON.stringify({ id, status })
            });
            if (res.ok) {
                showToast(`Status set to "${status}".`);
                loadDashboard();
            } else {
                showToast('Could not update status. Please try again.');
            }
        } catch (e) {
            console.error('Failed to update interaction status:', e);
            showToast('Network error while updating status.');
        }
    }

    // Search
    const semanticToggleBtn = document.getElementById('semantic-toggle-btn');
    if (semanticToggleBtn) {
        semanticToggleBtn.addEventListener('click', () => {
            const isActive = semanticToggleBtn.classList.toggle('active');
            semanticToggleBtn.classList.toggle('glow-purple');
            
            semanticToggleBtn.innerHTML = `<i data-lucide="${isActive ? 'brain' : 'network'}"></i>`;
            if (window.lucide) window.lucide.createIcons();
            
            if (isActive) {
                semanticToggleBtn.title = window.t("Semantic Search Active");
                globalSearchInput.placeholder = window.t("Semantic query (AI)...");
            } else {
                semanticToggleBtn.title = window.t("Keyword Search Active");
                globalSearchInput.placeholder = window.t("Quick graph search... (FTS5 matched)");
            }
        });
    }

    searchBtn.addEventListener('click', (e) => performSearch(e));
    globalSearchInput.addEventListener('keypress', (e) => {
        if (e.key === 'Enter') performSearch();
    });
    
    typeFilter.addEventListener('change', performSearch);
    
    triggerScanBtn.addEventListener('click', triggerScan);
    generateCapsuleBtn.addEventListener('click', generateCapsule);
    
    // Graph View Controls
    fitGraphBtn.addEventListener('click', () => {
        if (network) network.zoomToFit(1000);
    });
    
    clearGraphBtn.addEventListener('click', () => {
        graphData = { nodes: [], links: [] }; if(network) network.graphData(graphData);
        
        allDiscoveredNodes.clear();
        hideDrawer();
        resultsCountEl.textContent = "Graph cleared. Perform a search to load nodes.";
        resultsListEl.innerHTML = "";
        showDashboard();
        showToast("Visual workspace cleared.");
    });

    closeDrawerBtn.addEventListener('click', hideDrawer);
    
    closeModalBtn.addEventListener('click', () => {
        capsuleModal.classList.add('hidden');
    });

    copyCapsuleBtn.addEventListener('click', () => {
        capsuleText.select();
        navigator.clipboard.writeText(capsuleText.value);
        showToast("Capsule copied to clipboard!");
    });

    // Modal Tab Switching Logic
    document.querySelectorAll('.modal-tab').forEach(tab => {
        tab.addEventListener('click', () => {
            const targetTab = tab.getAttribute('data-tab');
            
            // Switch tab buttons
            document.querySelectorAll('.modal-tab').forEach(t => t.classList.remove('active'));
            tab.classList.add('active');
            
            // Switch tab content
            document.querySelectorAll('.modal-tab-content').forEach(c => c.classList.remove('active'));
            const tabContent = document.getElementById(targetTab);
            if (tabContent) tabContent.classList.add('active');
            
            // Load plugins when switching to plugins tab
            if (targetTab === 'plugins-tab') {
                loadPluginsList();
            }
            
            if (window.lucide) window.lucide.createIcons();
        });
    });

    // ============================
    // MODALS & NAVIGATION EVENTS
    // ============================

    // Layout Toggle via Dropdown
    const layoutSelect = document.getElementById('layout-select');

    if (layoutSelect) {
        layoutSelect.addEventListener('change', (e) => {
            const val = e.target.value;
            const mode = layoutModes.find(m => m.id === val) || layoutModes[0];

            physicsEnabled = mode.physics;
            
            if (network) {
                network.dagMode(mode.dagMode);
                // Finite cooldown so the simulation settles and stops burning CPU/GPU
                network.cooldownTicks(physicsEnabled ? 200 : 0);
                if (physicsEnabled) {
                    network.d3ReheatSimulation();
                }
            }
        });
    }

    // ============================
    // PROJECT DROPDOWN (Quick Switch)
    // ============================

    // Toggle dropdown on project button click
    activeProjectBtn.addEventListener('click', (e) => {
        e.stopPropagation();
        const isOpen = !projectDropdown.classList.contains('hidden');
        if (isOpen) {
            closeDropdown();
        } else {
            openDropdown();
        }
    });

    function openDropdown() {
        // Fetch fresh projects and render dropdown
        loadDropdownProjects();
        projectDropdown.classList.remove('hidden');
        activeProjectBtn.classList.add('open');
        if (dropdownSearchInput) {
            dropdownSearchInput.value = '';
            setTimeout(() => dropdownSearchInput.focus(), 50);
        }
        if (window.lucide) window.lucide.createIcons();
    }

    function closeDropdown() {
        projectDropdown.classList.add('hidden');
        activeProjectBtn.classList.remove('open');
    }

    // Click outside to dismiss dropdown
    document.addEventListener('click', (e) => {
        if (!projectDropdown.classList.contains('hidden')) {
            const insideDropdown = projectDropdown.contains(e.target);
            const insideBtn = activeProjectBtn.contains(e.target);
            if (!insideDropdown && !insideBtn) {
                closeDropdown();
            }
        }
    });

    // Escape key to dismiss dropdown
    document.addEventListener('keydown', (e) => {
        if (e.key === 'Escape' && !projectDropdown.classList.contains('hidden')) {
            closeDropdown();
        }
    });

    // Dropdown search input
    if (dropdownSearchInput) {
        dropdownSearchInput.addEventListener('input', () => {
            renderDropdownProjects(dropdownSearchInput.value.trim().toLowerCase());
        });
    }

    // Fetch projects for the dropdown
    async function loadDropdownProjects() {
        try {
            const res = await fetch('/api/projects');
            if (res.ok) {
                const data = await res.json();
                registeredProjects = (data.projects || []).map(p => ({
                    name: p.name || p.Name || '',
                    path: p.path || p.Path || '',
                    databasePath: p.databasePath || p.DatabasePath || ''
                }));
                activeProject = data.activeProject || data.ActiveProject || '';
                activeProjectName.textContent = activeProject || 'No Project';
                renderDropdownProjects('');
            }
        } catch (err) {
            console.error('Error loading projects for dropdown:', err);
        }
    }

    // Render the dropdown list
    function renderDropdownProjects(filter) {
        if (!dropdownProjectList) return;
        dropdownProjectList.innerHTML = '';

        const filtered = registeredProjects.filter(p => {
            if (!filter) return true;
            return p.name.toLowerCase().includes(filter) || p.path.toLowerCase().includes(filter);
        });

        if (filtered.length === 0) {
            dropdownProjectList.innerHTML = '';
            const emptyLi = document.createElement('li');
            emptyLi.className = 'dropdown-empty';
            emptyLi.textContent = window.t('No projects found.');
            dropdownProjectList.appendChild(emptyLi);
            return;
        }

        filtered.forEach(proj => {
            const isActive = proj.name.toLowerCase() === activeProject.toLowerCase();
            const li = document.createElement('li');
            li.className = `dropdown-project-item${isActive ? ' active' : ''}`;
            li.innerHTML = `
                <div class="dropdown-project-info">
                    <span class="dropdown-project-name">${proj.name}</span>
                    <span class="dropdown-project-path" title="${proj.path}">${proj.path}</span>
                </div>
                ${isActive ? '<span class="dropdown-active-badge">Active</span>' : ''}
            `;
            li.addEventListener('click', () => {
                if (!isActive) {
                    switchProject(proj.name);
                }
                closeDropdown();
            });
            dropdownProjectList.appendChild(li);
        });
    }

    // ============================
    // ============================
    // ADMIN MODAL (B2B SaaS)
    // ============================

    if (openAdminBtn) {
        openAdminBtn.addEventListener('click', () => {
            document.querySelectorAll('.modal-tab').forEach(t => {
                if (t.dataset.tab?.startsWith('admin')) t.classList.remove('active');
            });
            document.querySelectorAll('.modal-tab-content').forEach(c => {
                if (c.id.startsWith('admin')) c.classList.remove('active');
            });
            const orgsTabBtn = document.querySelector('.modal-tab[data-tab="admin-orgs-tab"]');
            const orgsTabContent = document.getElementById('admin-orgs-tab');
            if (orgsTabBtn) orgsTabBtn.classList.add('active');
            if (orgsTabContent) orgsTabContent.classList.add('active');

            loadAdminData();
            adminModal.classList.remove('hidden');
        });
    }

    if (closeAdminModalBtn) {
        closeAdminModalBtn.addEventListener('click', () => {
            adminModal.classList.add('hidden');
            document.getElementById('new-token-display').classList.add('hidden');
        });
    }

    // Modal Tabs logic (works for both modals)
    document.querySelectorAll('.modal-tab').forEach(tab => {
        tab.addEventListener('click', () => {
            const tabId = tab.getAttribute('data-tab');
            const parentHeader = tab.closest('.modal-header');
            const parentBody = tab.closest('.modal-container').querySelector('.modal-body');

            parentHeader.querySelectorAll('.modal-tab').forEach(t => t.classList.remove('active'));
            parentBody.querySelectorAll('.modal-tab-content').forEach(c => c.classList.remove('active'));

            tab.classList.add('active');
            const targetContent = document.getElementById(tabId);
            if (targetContent) targetContent.classList.add('active');
        });
    });

    async function loadAdminData() {
        try {
            const orgsResponse = await fetch('/api/admin/orgs');
            if (orgsResponse.ok) {
                const orgs = await orgsResponse.json();
                renderOrgsList(orgs);
                updateUserOrgSelects(orgs);
                
                // If there are orgs, load users for the first one by default
                const filterOrgSelect = document.getElementById('filter-users-org');
                if (orgs.length > 0 && !filterOrgSelect.value) {
                    filterOrgSelect.value = orgs[0].id;
                    loadUsers(orgs[0].id);
                } else if (filterOrgSelect.value) {
                    loadUsers(filterOrgSelect.value);
                }
            }
        } catch (error) {
            console.error('Failed to load admin data', error);
            showToast('Failed to load admin data');
        }
    }

    function renderOrgsList(orgs) {
        const orgsList = document.getElementById('orgs-list');
        orgsList.innerHTML = '';
        if (orgs.length === 0) {
            orgsList.innerHTML = '<div class="empty-state">No organizations found.</div>';
            return;
        }

        orgs.forEach(org => {
            const li = document.createElement('div');
            li.className = 'plugin-item';
            li.innerHTML = `
                <div class="plugin-info">
                    <div class="plugin-name"><i data-lucide="building"></i> ${org.name}</div>
                    <div class="plugin-desc">ID: ${org.id}</div>
                </div>
            `;
            orgsList.appendChild(li);
        });
        lucide.createIcons();
    }

    function updateUserOrgSelects(orgs) {
        const createSelect = document.getElementById('new-user-org');
        const filterSelect = document.getElementById('filter-users-org');
        
        // Preserve selections if possible
        const createVal = createSelect.value;
        const filterVal = filterSelect.value;

        createSelect.innerHTML = '<option value="">Select Organization...</option>';
        filterSelect.innerHTML = '<option value="">Select Organization to view users...</option>';

        orgs.forEach(org => {
            const opt1 = document.createElement('option');
            opt1.value = org.id;
            opt1.textContent = org.name;
            createSelect.appendChild(opt1);

            const opt2 = document.createElement('option');
            opt2.value = org.id;
            opt2.textContent = org.name;
            filterSelect.appendChild(opt2);
        });

        if (createVal) createSelect.value = createVal;
        if (filterVal) filterSelect.value = filterVal;
    }

    async function loadUsers(orgId) {
        if (!orgId) return;
        try {
            const res = await fetch(`/api/admin/users/${orgId}`);
            if (res.ok) {
                const users = await res.json();
                renderUsersList(users);
            }
        } catch (error) {
            console.error('Failed to load users', error);
        }
    }

    function renderUsersList(users) {
        const usersList = document.getElementById('users-list');
        usersList.innerHTML = '';
        if (users.length === 0) {
            usersList.innerHTML = '<div class="empty-state">No users found for this organization.</div>';
            return;
        }

        users.forEach(user => {
            const li = document.createElement('div');
            li.className = 'plugin-item';
            li.innerHTML = `
                <div class="plugin-info">
                    <div class="plugin-name"><i data-lucide="user"></i> ${user.username}</div>
                    <div class="plugin-desc">ID: ${user.id} | GitHub: ${user.gitHubUsername || 'None'}</div>
                </div>
                <div class="plugin-actions">
                    <button class="glass-btn hover-glow-red icon-only-btn" onclick="deleteUser('${user.id}')" title="Delete User">
                        <i data-lucide="trash-2"></i>
                    </button>
                </div>
            `;
            usersList.appendChild(li);
        });
        lucide.createIcons();
    }

    document.getElementById('create-org-btn')?.addEventListener('click', async () => {
        const input = document.getElementById('new-org-name');
        const name = input.value.trim();
        if (!name) return;

        try {
            const res = await fetch('/api/admin/orgs', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ name })
            });
            if (res.ok) {
                input.value = '';
                showToast('Organization created successfully');
                loadAdminData();
            } else {
                showToast('Failed to create organization');
            }
        } catch (error) {
            console.error(error);
            showToast('Error creating organization');
        }
    });

    document.getElementById('create-user-btn')?.addEventListener('click', async () => {
        const orgId = document.getElementById('new-user-org').value;
        const username = document.getElementById('new-user-name').value.trim();
        const github = document.getElementById('new-user-github').value.trim();

        if (!orgId || !username) {
            showToast('Please provide an organization and username');
            return;
        }

        try {
            const res = await fetch('/api/admin/users', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ organizationId: orgId, username, githubUsername: github })
            });

            if (res.ok) {
                const user = await res.json();
                document.getElementById('new-user-name').value = '';
                document.getElementById('new-user-github').value = '';
                
                // Show PAT
                const tokenDisplay = document.getElementById('new-token-display');
                const tokenInput = document.getElementById('new-pat-input');
                tokenInput.value = user.apiToken;
                tokenDisplay.classList.remove('hidden');

                // Reload users if the current view is for this org
                if (document.getElementById('filter-users-org').value === orgId) {
                    loadUsers(orgId);
                }
                
                showToast('User created and token generated');
            } else {
                showToast('Failed to create user');
            }
        } catch (error) {
            console.error(error);
            showToast('Error creating user');
        }
    });

    document.getElementById('filter-users-org')?.addEventListener('change', (e) => {
        loadUsers(e.target.value);
    });

    window.deleteUser = async function(userId) {
        if (!confirm('Are you sure you want to delete this user? Their token will be revoked immediately.')) return;
        
        try {
            const res = await fetch(`/api/admin/users/${userId}`, { method: 'DELETE' });
            if (res.ok) {
                showToast('User deleted successfully');
                const orgId = document.getElementById('filter-users-org').value;
                if (orgId) loadUsers(orgId);
            } else {
                showToast('Failed to delete user');
            }
        } catch (error) {
            console.error(error);
            showToast('Error deleting user');
        }
    };

    // ============================
    // SETTINGS MODAL (Config + Plugins)
    // ============================

    openSettingsBtn.addEventListener('click', () => {
        // Don't reset projectFilter or search query, let user see their last state
        // projectFilter = 'all';
        // projectSearchQuery = '';

        const projectSearchInput = document.getElementById('project-search-input');
        if (projectSearchInput) {
            projectSearchInput.value = '';
        }

        document.querySelectorAll('.filter-tab').forEach(b => {
            if (b.getAttribute('data-filter') === 'all') {
                b.classList.add('active');
            } else {
                b.classList.remove('active');
            }
        });

        // Ensure Projects tab is active on open
        document.querySelectorAll('.modal-tab').forEach(t => t.classList.remove('active'));
        document.querySelectorAll('.modal-tab-content').forEach(c => c.classList.remove('active'));
        const projectsTabBtn = document.querySelector('.modal-tab[data-tab="projects-tab"]');
        const projectsTabContent = document.getElementById('projects-tab');
        if (projectsTabBtn) projectsTabBtn.classList.add('active');
        if (projectsTabContent) projectsTabContent.classList.add('active');

        loadProjectsList();
        projectModal.classList.remove('hidden');
        if (window.lucide) {
            window.lucide.createIcons();
        }
    });

    closeProjectModalBtn.addEventListener('click', () => {
        projectModal.classList.add('hidden');
    });

    // Wire up Search Input
    const projectSearchInput = document.getElementById('project-search-input');
    if (projectSearchInput) {
        projectSearchInput.addEventListener('input', (e) => {
            projectSearchQuery = e.target.value;
            renderProjectsList();
        });
    }

    // Wire up Filter Tabs
    document.querySelectorAll('.filter-tab').forEach(btn => {
        btn.addEventListener('click', (e) => {
            document.querySelectorAll('.filter-tab').forEach(b => b.classList.remove('active'));
            btn.classList.add('active');
            projectFilter = btn.getAttribute('data-filter');
            renderProjectsList();
        });
    });

    addProjectBtn.addEventListener('click', handleAddProject);
    saveConfigBtn.addEventListener('click', handleSaveConfig);
    scanProjectBtn.addEventListener('click', handleScanProject);
    deleteProjectBtn.addEventListener('click', handleDeleteProject);
    activateSelectedProjectBtn.addEventListener('click', handleActivateSelectedProject);

    // Folder Browser Event Listeners
    browseFolderBtn.addEventListener('click', () => {
        // Open folder browser on current field value, or default workspace
        const currentFieldPath = newProjectPathInput.value.trim();
        openFolderBrowser(currentFieldPath || (registeredProjects.find(p => p.name.toLowerCase() === activeProject.toLowerCase())?.path) || "");
    });

    closeBrowseModalBtn.addEventListener('click', () => {
        browseModal.classList.add('hidden');
    });

    browseUpBtn.addEventListener('click', () => {
        const parentPath = browseUpBtn.dataset.parentPath;
        if (parentPath) {
            navigateFolderBrowser(parentPath);
        }
    });

    browseGoBtn.addEventListener('click', () => {
        const customPath = browseCurrentPathInput.value.trim();
        if (customPath) {
            navigateFolderBrowser(customPath);
        }
    });

    browseCurrentPathInput.addEventListener('keydown', (e) => {
        if (e.key === 'Enter') {
            const customPath = browseCurrentPathInput.value.trim();
            if (customPath) navigateFolderBrowser(customPath);
        }
    });

    browseCancelBtn.addEventListener('click', () => {
        browseModal.classList.add('hidden');
    });

    browseConfirmBtn.addEventListener('click', () => {
        if (browseSelectedPath) {
            newProjectPathInput.value = browseSelectedPath;
            // Update input path
            newProjectPathInput.dispatchEvent(new Event('input'));
            browseModal.classList.add('hidden');
            showToast(`Selected directory: ${browseSelectedPath}`);
        } else {
            showToast("Please select a folder first.");
        }
    });

    // GRAPH VIEWPORT - FILTERS & INTERACTION LISTENERS
    // GRAPH VIEWPORT - DYNAMIC FILTERS
    async function initFilters() {
        try {
            const res = await fetch('/api/node-types');
            if (res.ok) {
                const data = await res.json();
                allKnownTypes = data.types || [];
                
                // Initialize activeTypes based on isVisibleByDefault
                activeTypes = new Set();
                allKnownTypes.forEach(t => {
                    if (t.isVisibleByDefault) {
                        activeTypes.add(t.typeName);
                    }
                });
                
                updateFilterBar();
                renderLegend();
            }
        } catch (err) {
            console.error("Failed to load node types:", err);
        }
    }

    function renderLegend() {
        const legendContainer = document.getElementById('dynamic-legend');
        if (!legendContainer) return;
        
        legendContainer.innerHTML = '';

        const presentTypes = new Set();
        for (const [id, node] of allDiscoveredNodes.entries()) {
            if (node.Type) presentTypes.add(node.Type);
        }

        const typesToRender = Array.from(activeTypes).filter(t => presentTypes.has(t)).sort();
        
        typesToRender.forEach(type => {
            const item = document.createElement('div');
            item.className = 'legend-item';
            
            const colorSpan = document.createElement('span');
            colorSpan.className = 'legend-color';
            colorSpan.style.backgroundColor = getNodeColorAndStyle(type).color.border;
            
            const textSpan = document.createElement('span');
            textSpan.textContent = type;
            
            item.appendChild(colorSpan);
            item.appendChild(textSpan);
            legendContainer.appendChild(item);
        });
    }

    // GRAPH VIEWPORT - FILTERS & INTERACTION LISTENERS
    // GRAPH VIEWPORT - DYNAMIC FILTERS
    function updateFilterBar() {
        const filterBar = document.getElementById('type-filter-bar');
        if (!filterBar) return;

        // W-1 Fix: Do not destroy the static "Filter:" label, just remove the old dropdown container
        const existingDropdown = filterBar.querySelector('.filter-dropdown-container');
        if (existingDropdown) {
            filterBar.removeChild(existingDropdown);
        }
        
        // Use the types actually present in the database rather than just the loaded graph
        const filteredKnownTypes = allKnownTypes.filter(t => databaseNodeTypes.has(t.typeName));

        // Group by category
        const grouped = {};
        filteredKnownTypes.forEach(t => {
            const cat = t.category || 'Other';
            if (!grouped[cat]) grouped[cat] = [];
            grouped[cat].push(t);
        });

        // --- 1. Update the Search Bar Custom Dropdown ---
        const searchTypeMenu = document.getElementById('search-type-menu');
        const searchTypeLabel = document.getElementById('search-type-label');
        const searchTypeInput = document.getElementById('type-filter');
        
        if (searchTypeMenu) {
            searchTypeMenu.innerHTML = '';
            
            // "All Types" Option
            const allOption = document.createElement('div');
            allOption.className = 'filter-dropdown-item';
            allOption.innerHTML = `<span>All Types</span>`;
            allOption.addEventListener('click', () => {
                searchTypeInput.value = '';
                searchTypeLabel.textContent = 'All Types';
                searchTypeMenu.classList.add('hidden');
                performSearch(); // Auto-search on change
            });
            searchTypeMenu.appendChild(allOption);
            
            Object.keys(grouped).sort().forEach(cat => {
                const catHeader = document.createElement('div');
                catHeader.className = 'filter-category-header';
                catHeader.textContent = cat;
                searchTypeMenu.appendChild(catHeader);
                
                const items = grouped[cat].sort((a, b) => a.typeName.localeCompare(b.typeName));
                items.forEach(t => {
                    const itemDiv = document.createElement('div');
                    itemDiv.className = 'filter-dropdown-item';
                    itemDiv.innerHTML = `<span>${t.typeName}</span>`;
                    
                    itemDiv.addEventListener('click', () => {
                        searchTypeInput.value = t.typeName;
                        searchTypeLabel.textContent = t.typeName;
                        searchTypeMenu.classList.add('hidden');
                        performSearch(); // Auto-search on change
                    });
                    
                    searchTypeMenu.appendChild(itemDiv);
                });
            });
        }

        // --- 2. Build the Category-based Filter Dropdown ---
        const dropdownContainer = document.createElement('div');
        dropdownContainer.className = 'filter-dropdown-container';

        const dropdownBtn = document.createElement('button');
        dropdownBtn.className = 'glass-btn filter-dropdown-btn';
        dropdownBtn.innerHTML = '<i data-lucide="list-filter"></i> <span>Filter Nodes</span> <i data-lucide="chevron-down"></i>';
        
        const dropdownMenu = document.createElement('div');
        dropdownMenu.className = 'filter-dropdown-menu glass-panel hidden';

        Object.keys(grouped).sort().forEach(cat => {
            const catHeader = document.createElement('div');
            catHeader.className = 'filter-category-header';
            catHeader.textContent = cat;
            dropdownMenu.appendChild(catHeader);

            const items = grouped[cat].sort((a, b) => a.typeName.localeCompare(b.typeName));
            items.forEach(t => {
                const itemDiv = document.createElement('div');
                itemDiv.className = 'filter-dropdown-item';
                
                const checkbox = document.createElement('input');
                checkbox.type = 'checkbox';
                checkbox.id = `filter-cb-${t.typeName}`;
                checkbox.checked = activeTypes.has(t.typeName);
                
                const label = document.createElement('label');
                label.htmlFor = checkbox.id;
                label.textContent = t.typeName;

                checkbox.addEventListener('change', () => {
                    if (checkbox.checked) {
                        activeTypes.add(t.typeName);
                    } else {
                        activeTypes.delete(t.typeName);
                    }
                    
                    renderLegend(); // update legend immediately
                    
                    if (network) {
                        const currentFilteredNodes = getFilteredNodes();
                        const validNodes = new Set(currentFilteredNodes.map(n => n.id));
                        const filteredData = {
                            nodes: currentFilteredNodes,
                            links: graphData.links.filter(link => {
                                const s = link.source.id || link.source;
                                const tgt = link.target.id || link.target;
                                return validNodes.has(s) && validNodes.has(tgt);
                            })
                        };
                        network.graphData(filteredData);
                    }
                    displaySearchResults(lastSearchResults);
                });

                itemDiv.appendChild(checkbox);
                itemDiv.appendChild(label);
                dropdownMenu.appendChild(itemDiv);
            });
        });

        dropdownBtn.addEventListener('click', (e) => {
            e.stopPropagation();
            document.querySelectorAll('.filter-dropdown-menu').forEach(m => {
                if (m !== dropdownMenu) m.classList.add('hidden');
            });
            dropdownMenu.classList.toggle('hidden');
        });
        
        // Search-type and global click-away listeners: register ONCE
        if (!window.__filterMenuListenerAttached) {
            const searchTypeBtn = document.getElementById('search-type-btn');
            if (searchTypeBtn) {
                searchTypeBtn.addEventListener('click', (e) => {
                    e.stopPropagation();
                    const stMenu = document.getElementById('search-type-menu');
                    // Close all OTHER menus first
                    document.querySelectorAll('.filter-dropdown-menu').forEach(m => {
                        if (m !== stMenu) m.classList.add('hidden');
                    });
                    if (stMenu) stMenu.classList.toggle('hidden');
                });
            }

            document.addEventListener('click', (e) => {
                if (!e.target.closest('.filter-dropdown-container')) {
                    document.querySelectorAll('.filter-dropdown-menu').forEach(m => m.classList.add('hidden'));
                }
            });
            window.__filterMenuListenerAttached = true;
        }

        dropdownContainer.appendChild(dropdownBtn);
        dropdownContainer.appendChild(dropdownMenu);
        filterBar.appendChild(dropdownContainer);

        if (window.lucide) window.lucide.createIcons();
    }

    // Toggle Drag Select Mode
    const dragSelectBtn = document.getElementById('drag-select-btn');
    if (dragSelectBtn) {
        dragSelectBtn.addEventListener('click', () => {
            dragSelectMode = !dragSelectMode;
            dragSelectBtn.classList.toggle('active', dragSelectMode);

            if (network) {
                // ForceGraph uses enablePanInteraction instead of setOptions
                network.enablePanInteraction(!dragSelectMode);
            }
            showToast(dragSelectMode ? "Drag Select Active! Drag a box on the canvas to select multiple nodes." : "Normal drag-pan mode active.");
        });
    }

    // Drag Select Bounding Box Canvas Events
    const graphContainer = document.getElementById('network-graph');
    let isDrawingSelection = false;
    let startX = 0, startY = 0;

    if (graphContainer) {
        graphContainer.addEventListener('mousedown', (e) => {
            if (!dragSelectMode) return;

            const rect = graphContainer.getBoundingClientRect();
            startX = e.clientX;
            startY = e.clientY;

            let selectionBox = document.getElementById('selection-box');
            if (!selectionBox) {
                selectionBox = document.createElement('div');
                selectionBox.id = 'selection-box';
                selectionBox.className = 'selection-box';
                graphContainer.appendChild(selectionBox);
            }

            selectionBox.style.left = (startX - rect.left) + 'px';
            selectionBox.style.top = (startY - rect.top) + 'px';
            selectionBox.style.width = '0px';
            selectionBox.style.height = '0px';
            selectionBox.style.display = 'block';

            isDrawingSelection = true;
        });

        window.addEventListener('mousemove', (e) => {
            if (!isDrawingSelection) return;

            const rect = graphContainer.getBoundingClientRect();
            const x1 = Math.min(startX, e.clientX) - rect.left;
            const y1 = Math.min(startY, e.clientY) - rect.top;
            const x2 = Math.max(startX, e.clientX) - rect.left;
            const y2 = Math.max(startY, e.clientY) - rect.top;

            const selectionBox = document.getElementById('selection-box');
            if (selectionBox) {
                selectionBox.style.left = x1 + 'px';
                selectionBox.style.top = y1 + 'px';
                selectionBox.style.width = (x2 - x1) + 'px';
                selectionBox.style.height = (y2 - y1) + 'px';
            }
        });

        window.addEventListener('mouseup', (e) => {
            if (!isDrawingSelection) return;
            isDrawingSelection = false;

            const selectionBox = document.getElementById('selection-box');
            if (selectionBox) {
                selectionBox.style.display = 'none';
            }

            const rect = graphContainer.getBoundingClientRect();
            const x1 = Math.min(startX, e.clientX) - rect.left;
            const y1 = Math.min(startY, e.clientY) - rect.top;
            const x2 = Math.max(startX, e.clientX) - rect.left;
            const y2 = Math.max(startY, e.clientY) - rect.top;

            if (network) {
                const c1 = network.screen2GraphCoords(x1, y1);
                const c2 = network.screen2GraphCoords(x2, y2);

                const minX = Math.min(c1.x, c2.x);
                const maxX = Math.max(c1.x, c2.x);
                const minY = Math.min(c1.y, c2.y);
                const maxY = Math.max(c1.y, c2.y);

                const selectedIds = [];
                const currentVisibleNodes = getFilteredNodes();

                for (const node of currentVisibleNodes) {
                    // node.x and node.y are populated by force-graph
                    if (node.x >= minX && node.x <= maxX && node.y >= minY && node.y <= maxY) {
                        selectedIds.push(node.id);
                    }
                }

                if (selectedIds.length > 0) {
                    // ForceGraph doesn't have native multi-select, but we select the first one for the drawer
                    selectNode(selectedIds[0]);

                    const capsuleQueryInput = document.getElementById('capsule-query');
                    if (capsuleQueryInput) {
                        const names = selectedIds.map(id => {
                            const n = allDiscoveredNodes.get(id) || graphData.nodes.find(n => n.id === id);
                            return n ? n.Name : id;
                        });
                        capsuleQueryInput.value = names.join(', ');
                    }

                    showToast(`Selected ${selectedIds.length} nodes! Copied to Capsule seeds.`);
                }
            }
        });
    }

    // WebGL automatically stores node positions via the data objects
    function saveGraphPositions() {
        // No-op for force-graph
    }

    // NETWORK INITIALIZATION (WebGL)
    function initNetwork() {
        const container = document.getElementById('network-graph');
        
        network = ForceGraph()(container)
            .graphData(graphData)
            .cooldownTicks(physicsEnabled ? 200 : 0)
            .nodeId('id')
            .nodeLabel(node => `<div style="background:rgba(0,0,0,0.8); padding:5px; border-radius:4px; border:1px solid ${node.color}">
                                    <strong>${node.type}</strong><br/>${node.name}
                                </div>`)
            .nodeColor(node => node.color || '#3b82f6')
            .nodeVal(node => node.val || 10)
            .linkColor(() => 'rgba(148, 163, 184, 0.3)')
            .linkWidth(1)
            .linkDirectionalArrowLength(3.5)
            .linkDirectionalArrowRelPos(1)
            .backgroundColor('transparent')
            .onNodeClick(node => {
                selectNode(node.id);
            })
            .onNodeHover(node => {
                container.style.cursor = node ? 'pointer' : 'grab';
            })
            .nodeCanvasObject((node, ctx, globalScale) => {
                const label = node.name || node.id;
                const fontSize = 12 / globalScale;
                ctx.font = `${fontSize}px Outfit, sans-serif`;
                
                const radius = Math.sqrt(node.val || 10) * 2 / globalScale;
                
                // Handle legacy vis-network color objects
                const bgColor = (typeof node.color === 'object' && node.color.background) ? node.color.background : (node.color || '#3b82f6');
                const borderColor = (typeof node.color === 'object' && node.color.border) ? node.color.border : 'rgba(255, 255, 255, 0.4)';
                
                // Draw circle
                ctx.beginPath();
                ctx.arc(node.x, node.y, radius, 0, 2 * Math.PI, false);
                ctx.fillStyle = bgColor;
                ctx.fill();
                ctx.strokeStyle = borderColor;
                ctx.lineWidth = 1.5 / globalScale;
                ctx.stroke();

                // Draw label
                if (globalScale > 1.2) {
                    ctx.textAlign = 'center';
                    ctx.textBaseline = 'middle';
                    ctx.fillStyle = '#e2e8f0';
                    ctx.fillText(label, node.x, node.y + radius + (fontSize * 1.2));
                }
            })
            .onBackgroundClick(() => {
                currentSelectedNodeId = null;
                detailsDrawer.classList.add('hidden');
            });
            
        // Configure physics
        network.d3Force('charge').strength(-150);
        network.d3Force('link').distance(50);
    }

    // Handle window resize dynamically (registered once, not per initNetwork call,
    // to avoid stacking duplicate listeners when the graph is re-initialized)
    window.addEventListener('resize', () => {
        const container = document.getElementById('network-graph');
        if (network && container) {
            network.width(container.clientWidth);
            network.height(container.clientHeight);
        }
    });

    // LOAD GRAPH DEFAULT STATE (Index files from current directory)
    async function loadDefaultGraph() {
        updateLoadingState("Initializing default graph state...", true);
        try {
            // Load some sample seeds if available. Let's do a search for empty/any to populate some visual elements on startup
            const res = await fetch('/api/search?q=*&limit=150');
            if (res.ok) {
                updateLoadingState("Rendering default components...", true);
                const data = await res.json();
                if (data.length > 0) {
                    data.forEach(normalizeResult);
                    displaySearchResults(data);
                    renderSearchInGraph(data);
                    updateFilterBar();
                } else {
                    updateLoadingState(null, false);
                }
            } else {
                updateLoadingState(null, false);
            }
        } catch (err) {
            updateLoadingState(null, false);
            console.error("Could not load default graph data on startup:", err);
        }
    }

    // LOAD GENERAL STATISTICS
    async function loadStats() {
        try {
            const res = await fetch('/api/stats');
            if (res.ok) {
                const stats = await res.json();
                statsNodesEl.textContent = stats.totalNodes;
                statsEdgesEl.textContent = stats.totalEdges;
                
                if (stats.nodesByType) {
                    databaseNodeTypes = new Set(Object.keys(stats.nodesByType));
                } else {
                    databaseNodeTypes.clear();
                }
                updateFilterBar();
            }
        } catch (err) {
            console.error("Error loading statistics:", err);
        }
    }

    // PERFORM FTS5 GRAPH SEARCH
    let currentSearchOffset = 0;
    let currentSearchLimit = 20;
    let currentSearchQuery = "";

    async function performSearch(queryOrEvent, isLoadMore = false) {
        let query = (typeof queryOrEvent === 'string') ? queryOrEvent : globalSearchInput.value.trim();
        if (queryOrEvent instanceof Event) {
            isLoadMore = false; // ensure event doesn't bleed into loadMore
        }
        
        if (!query && !isLoadMore) return;

        hideDashboard();

        if (!isLoadMore) {
            currentSearchOffset = 0;
            currentSearchQuery = query;
            resultsCountEl.textContent = window.t("Searching...");
            resultsListEl.innerHTML = "";
            lastSearchResults = [];
        } else {
            const loadMoreBtn = document.getElementById('load-more-btn');
            if (loadMoreBtn) {
                loadMoreBtn.textContent = window.t("Loading...");
                loadMoreBtn.disabled = true;
            }
        }
        
        updateLoadingState(window.t("Querying database..."), true);

        try {
            currentSearchLimit = 20;
            if (currentSearchQuery.toLowerCase() === 'all' || currentSearchQuery === '*') {
                currentSearchLimit = 2000;
            }
            
            const typeFilter = document.getElementById('type-filter').value;
            const semanticToggleBtn = document.getElementById('semantic-toggle-btn');
            const isSemantic = semanticToggleBtn && semanticToggleBtn.classList.contains('active');
            
            let url = isSemantic 
                ? `/api/search/semantic?q=${encodeURIComponent(currentSearchQuery)}&limit=${currentSearchLimit}`
                : `/api/search?q=${encodeURIComponent(currentSearchQuery)}&limit=${currentSearchLimit}&offset=${currentSearchOffset}`;
                
            if (typeFilter && !isSemantic) {
                url += `&type=${encodeURIComponent(typeFilter)}`;
            }
            
            const res = await fetch(url);
            if (res.ok) {
                updateLoadingState(window.t("Processing results..."), true);
                const results = await res.json();
                results.forEach(normalizeResult);
                
                if (isLoadMore) {
                    lastSearchResults = lastSearchResults.concat(results);
                } else {
                    lastSearchResults = results;
                }

                resultsCountEl.textContent = `${window.t("Found")} ${lastSearchResults.length} ${window.t("matched node(s)")}:`;
                
                const hasMore = results.length === currentSearchLimit;
                displaySearchResults(lastSearchResults, hasMore);
                
                updateLoadingState(window.t("Building graph..."), true);
                renderSearchInGraph(lastSearchResults);
                updateFilterBar();

                // Show Ask AI button if results are found
                if (askAiBtn && lastSearchResults.length > 0) {
                    askAiBtn.classList.remove('hidden');
                }
            } else {
                updateLoadingState(null, false);
                resultsCountEl.textContent = window.t("Search failed.");
                showToast(window.t("Search failed. Verify query syntax."));
            }
        } catch (err) {
            updateLoadingState(null, false);
            resultsCountEl.textContent = "Search error: " + (err.message || String(err));
            console.error(window.t("Search failed:"), err);
        }
    }

    // DISPLAY SIDEBAR RESULTS
    function displaySearchResults(results, hasMore = false) {
        resultsListEl.innerHTML = "";
        lastSearchResults = results || [];
        
        const filtered = lastSearchResults.filter(res => {
            const node = res.Node;
            return activeTypes.has(node.Type);
        });

        resultsCountEl.textContent = `${window.t("Found")} ${filtered.length} ${window.t("matching nodes")}`;

        filtered.forEach(res => {
            const node = res.Node;
            allDiscoveredNodes.set(node.Id, node); // Add to cache

            const li = document.createElement('li');
            li.className = 'result-item';
            
            const badgeClass = getNodeBadgeClass(node.Type);
            const relativePath = getRelativeFilePath(node.FilePath);
            
            li.innerHTML = `
                <div class="result-item-header">
                    <div class="node-name" title="${escapeHtml(node.Name)}">${escapeHtml(node.Name)}</div>
                    <span class="node-type-badge ${badgeClass}">${node.Type}</span>
                </div>
                <div class="node-path" title="${node.FilePath}">${relativePath}</div>
            `;

            li.addEventListener('click', () => {
                // Focus in Graph
                if (network) {
                    const targetNode = graphData.nodes.find(n => n.id === node.Id);
                    if (targetNode) {
                        network.centerAt(targetNode.x, targetNode.y, 1000);
                        network.zoom(2, 1000);
                    }
                }
                selectNode(node.Id);
            });

            resultsListEl.appendChild(li);
        });

        if (hasMore) {
            const loadMoreLi = document.createElement('li');
            loadMoreLi.style.textAlign = 'center';
            loadMoreLi.style.padding = '0.5rem';
            const loadMoreBtn = document.createElement('button');
            loadMoreBtn.id = 'load-more-btn';
            loadMoreBtn.className = 'btn btn-secondary';
            loadMoreBtn.style.width = '100%';
            loadMoreBtn.textContent = window.t('Load More');
            loadMoreBtn.addEventListener('click', () => {
                currentSearchOffset += currentSearchLimit;
                performSearch(currentSearchQuery, true);
            });
            loadMoreLi.appendChild(loadMoreBtn);
            resultsListEl.appendChild(loadMoreLi);
        }
    }

    // RENDER NODES AND EDGES IN VIS GRAPH CONTAINER
    // Performance caps so a broad query (e.g. "*") cannot freeze the browser:
    // the WebGL canvas stays responsive and we avoid firing hundreds of subgraph requests.
    const MAX_GRAPH_NODES = 600;        // max nodes rendered into the canvas at once
    const MAX_SEED_EXPANSION = 200;     // max seeds we auto-expand via /api/subgraph

    function renderSearchInGraph(searchResults) {
        let newNodesCount = 0;

        // Only render up to MAX_GRAPH_NODES into the canvas; the sidebar list still shows all hits.
        const capped = searchResults.length > MAX_GRAPH_NODES
            ? searchResults.slice(0, MAX_GRAPH_NODES)
            : searchResults;

        if (searchResults.length > MAX_GRAPH_NODES) {
            showToast(`Showing first ${MAX_GRAPH_NODES} of ${searchResults.length} results in the graph. Refine your search or click a node to expand.`);
        }

        capped.forEach(res => {
            const node = res.node || res.Node;
            if (!node) return;

            if (!allDiscoveredNodes.has(node.Id)) {
                allDiscoveredNodes.set(node.Id, node);
                const style = getNodeColorAndStyle(node.Type);
                const n = {
                    id: node.Id,
                    name: node.Name,
                    type: node.Type,
                    group: node.Type,
                    color: style.color,
                    val: style.size,
                    ...getClusterCoords(node.Type)
                };
                graphData.nodes.push(n);
                newNodesCount++;
            }
        });

        if (newNodesCount > 0 && network) {
            // Trigger a debounced update via mergeGraphData logic or manually here
            // We'll let mergeGraphData handle the network update if edges are found,
            // but we must ensure it renders even if 0 edges are found.
            scheduleGraphDataUpdate();
        }

        const seedNodeIds = capped.map(r => r.node?.id || r.Node?.Id).filter(id => id);
        if (seedNodeIds.length === 0) return;

        // Auto-expand relations only for a bounded number of seeds to avoid a request storm.
        // For very large result sets the user expands on demand by clicking a node instead.
        const seedsToExpand = seedNodeIds.slice(0, MAX_SEED_EXPANSION);
        const chunkSize = 15;
        for (let i = 0; i < seedsToExpand.length; i += chunkSize) {
            const chunk = seedsToExpand.slice(i, i + chunkSize);
            fetchSubgraph(chunk, 0);
        }
    }

    // FETCH SUBGRAPH AND MERGE TO CANVAS DATASET
    async function fetchSubgraph(seedIds, hops = 1) {
        updateLoadingState("Fetching subgraph relations...", true);
        try {
            const res = await fetch(`/api/subgraph?seeds=${encodeURIComponent(seedIds.join(','))}&hops=${hops}`);
            if (res.ok) {
                updateLoadingState("Parsing graph data...", true);
                const data = await res.json();
                const normalizedNodes = (data.nodes || data.Nodes || []).map(normalizeNode);
                const normalizedEdges = (data.edges || data.Edges || []).map(normalizeEdge);
                mergeGraphData(normalizedNodes, normalizedEdges);
            } else {
                updateLoadingState(null, false);
            }
        } catch (err) {
            updateLoadingState(null, false);
            console.error("Failed to fetch connected subgraph:", err);
        }
    }

    // DOUBLE CLICK -> EXPAND FROM NODE
    function expandSubgraph(nodeId) {
        showToast(`Expanding graph around: ${allDiscoveredNodes.get(nodeId)?.Name || nodeId}`);
        fetchSubgraph([nodeId], 1);
    }

    // MERGE WEBGL GRAPH DATA
    let graphDataUpdateTimer = null;
    function scheduleGraphDataUpdate() {
        if (!network) return;
        clearTimeout(graphDataUpdateTimer);
        graphDataUpdateTimer = setTimeout(() => {
            const currentFilteredNodes = getFilteredNodes();
            const validNodes = new Set(currentFilteredNodes.map(n => n.id));
            
            const filteredData = {
                nodes: currentFilteredNodes,
                links: graphData.links.filter(link => {
                    const s = link.source.id || link.source;
                    const tgt = link.target.id || link.target;
                    return validNodes.has(s) && validNodes.has(tgt);
                })
            };
            network.graphData(filteredData);
            updateLoadingState(null, false);
        }, 150);
    }

    function mergeGraphData(nodes, edges) {
        updateLoadingState("Injecting nodes into graph...", true);
        const currentNodes = new Set(graphData.nodes.map(n => n.id));
        const currentEdges = new Set(graphData.links.map(e => e.id));
        
        const newNodes = [];
        const newEdges = [];

        nodes.forEach(node => {
            allDiscoveredNodes.set(node.Id, node); // Keep cache up to date

            if (!currentNodes.has(node.Id)) {
                const style = getNodeColorAndStyle(node.Type);
                
                newNodes.push({
                    id: node.Id,
                    name: node.Name,
                    color: style.color,
                    size: style.size,
                    type: node.Type,
                    val: style.size,
                    filePath: node.FilePath,
                    ...getClusterCoords(node.Type)
                });
            }
        });

        edges.forEach(edge => {
            const edgeId = `${edge.SourceId}-${edge.TargetId}-${edge.Relationship}`;
            if (!currentEdges.has(edgeId)) {
                newEdges.push({
                    id: edgeId,
                    source: edge.SourceId,
                    target: edge.TargetId,
                    name: edge.Relationship
                });
            }
        });

        if (newNodes.length > 0 || newEdges.length > 0) {
            graphData = {
                nodes: [...graphData.nodes, ...newNodes],
                links: [...graphData.links, ...newEdges]
            };
            
            if (network) {
                scheduleGraphDataUpdate();
            }
        }
        
        updateFilterBar();
    }

    // DETAILED SELECTION DRAWER
    // Renders the impact-analysis lists in the details drawer: who references this node (incoming) and
    // what it depends on (outgoing), each grouped, summarized, and clickable to navigate.
    function renderNodeReferences(incoming, outgoing) {
        drawerNodeRelations.innerHTML = "";

        const addGroup = (label, items, direction, emptyText) => {
            const header = document.createElement('li');
            header.className = 'rel-group-header';
            header.textContent = `${label} (${items.length})`;
            drawerNodeRelations.appendChild(header);

            if (items.length === 0) {
                const empty = document.createElement('li');
                empty.className = 'rel-empty';
                empty.textContent = emptyText;
                drawerNodeRelations.appendChild(empty);
                return;
            }

            items.forEach(item => {
                const li = document.createElement('li');
                li.className = 'relation-link';
                const summary = item.summary
                    ? `<span class="rel-summary">${escapeHtml(item.summary)}</span>` : '';
                li.innerHTML = `
                    <div class="rel-row">
                        <span class="rel-target" title="${escapeHtml(item.id)}">${escapeHtml(item.name)}</span>
                        <span class="rel-type">${direction} ${escapeHtml(item.relation)}</span>
                    </div>
                    ${summary}
                `;
                li.addEventListener('click', () => navigateToNode(item));
                drawerNodeRelations.appendChild(li);
            });
        };

        addGroup(window.t("Referenced by"), incoming, "←",
            window.t("Nothing references this — safe to change in isolation."));
        addGroup(window.t("Depends on"), outgoing, "→",
            window.t("Self-contained — depends on nothing."));
    }

    // Opens a reference's details. Off-screen references aren't in the rendered graph, so we stash a
    // lightweight stub first (so selectNode can show name/type/summary), then center the graph if present.
    function navigateToNode(item) {
        const id = item.id;
        if (!allDiscoveredNodes.has(id)) {
            allDiscoveredNodes.set(id, {
                Id: id, Type: item.type, Name: item.name,
                Summary: item.summary, FilePath: item.filePath, Properties: {}
            });
        }
        if (network) {
            const partner = graphData.nodes.find(n => n.id === id);
            if (partner && partner.x !== undefined) {
                network.centerAt(partner.x, partner.y, 1000);
                network.zoom(2, 1000);
            }
        }
        selectNode(id);
    }

    async function selectNode(nodeId) {
        currentSelectedNodeId = nodeId;
        const node = allDiscoveredNodes.get(nodeId);
        if (!node) return;

        // Render Details
        drawerNodeType.textContent = node.Type;
        drawerNodeType.className = `type-badge ${getNodeBadgeClass(node.Type)}`;
        drawerNodeName.textContent = node.Name;
        drawerNodePath.textContent = node.FilePath || "Virtual Namespace Node";

        // AI Summary section
        const drawerSummarySection = document.getElementById('drawer-summary-section');
        const drawerNodeSummary = document.getElementById('drawer-node-summary');
        if (node.Summary) {
            drawerNodeSummary.textContent = node.Summary;
            drawerSummarySection.style.display = 'flex';
        } else {
            drawerSummarySection.style.display = 'none';
        }

        // Properties section
        drawerNodeProperties.innerHTML = "";
        const properties = node.Properties || {};
        
        let hasProps = false;
        // Core fields are shown elsewhere; match case-insensitively (keys arrive as camelCase).
        const coreFields = new Set(["content", "filepath", "startline", "endline", "contenthash"]);
        for (const [key, value] of Object.entries(properties)) {
            if (coreFields.has(key.toLowerCase())) continue;

            // Skip empty / null-ish values so the drawer doesn't show "startLine null" noise.
            if (value === null || value === undefined) continue;
            const v = String(value).trim();
            if (v === '' || v.toLowerCase() === 'null') continue;

            hasProps = true;
            const propEl = document.createElement('div');
            propEl.className = 'prop-pill';
            propEl.innerHTML = `<span class="prop-key">${escapeHtml(key)}</span><span class="prop-val">${escapeHtml(v)}</span>`;
            drawerNodeProperties.appendChild(propEl);
        }

        const propsSec = document.getElementById('drawer-properties-section');
        if (hasProps) {
            propsSec.style.display = 'flex';
        } else {
            propsSec.style.display = 'none';
        }

        // Impact analysis from the FULL graph DB (not just on-screen edges): incoming = "referenced
        // by", outgoing = "depends on". Fetched via /api/node/references (one incident-edge query).
        drawerNodeRelations.innerHTML =
            `<li class="rel-empty">${escapeHtml(window.t("Loading references…"))}</li>`;
        try {
            const refRes = await fetch(`/api/node/references?id=${encodeURIComponent(nodeId)}`);
            // Drop a stale response if the user has since selected a different node.
            if (currentSelectedNodeId !== nodeId) return;
            if (!refRes.ok) throw new Error(`HTTP ${refRes.status}`);
            const refs = await refRes.json();
            renderNodeReferences(refs.incoming || [], refs.outgoing || []);
        } catch (err) {
            console.error("Failed to load node references:", err);
            if (currentSelectedNodeId === nodeId) {
                drawerNodeRelations.innerHTML =
                    `<li class="rel-empty">${escapeHtml(window.t("Could not load references."))}</li>`;
            }
        }

        // Code/Content syntax highlight preview
        const content = properties.Content || "";
        const codeElement = drawerNodeCode;
        
        // Remove existing Prism classes, detect language classes
        codeElement.className = "";
        const langClass = getPrismLanguageClass(node.FilePath || "");
        codeElement.classList.add(langClass);

        if (content) {
            codeElement.textContent = content.trim();
        } else {
            codeElement.textContent = `// No source code or block contents available for this [${node.Type}] structural definition.`;
        }
        
        try {
            if (window.Prism) {
                Prism.highlightElement(codeElement);
            }
        } catch (e) {
            console.error("Prism syntax highlighting failed:", e);
        }

        // Slide in Drawer
        detailsDrawer.classList.remove('hidden');
        
        // Auto load seed in capsule synthesis form for quick context generation
        if (node.Type.toLowerCase() !== "file") {
            capsuleQueryInput.value = node.Name;
        }
    }

    function hideDrawer() {
        detailsDrawer.classList.add('hidden');
        currentSelectedNodeId = null;
    }

    // CAPSULE SYNTHESIS GENERATION
    async function generateCapsule() {
        const query = capsuleQueryInput.value.trim();
        const hops = parseInt(capsuleHopsSelect.value);

        if (!query) {
            showToast("Enter a seed query for context generation.");
            return;
        }

        showToast("Synthesizing context capsule...");
        generateCapsuleBtn.disabled = true;

        try {
            const res = await fetch('/api/capsule', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ query, hops })
            });

            if (res.ok) {
                const data = await res.json();
                
                modalNodesCount.textContent = data.nodeCount;
                modalEdgesCount.textContent = data.edgeCount;
                capsuleText.value = data.markdown;
                
                capsuleModal.classList.remove('hidden');
            } else {
                const err = await res.text();
                showToast(`Failed: ${err || 'No seeds matched'}`);
            }
        } catch (err) {
            console.error("Capsule generation error:", err);
            showToast("Error generating capsule.");
        } finally {
            generateCapsuleBtn.disabled = false;
        }
    }

    // ON DEMAND SOURCE SCANNING / INDEXER TRIGGER
    async function triggerScan() {
        const activeProj = registeredProjects.find(p => p.name.toLowerCase() === activeProject.toLowerCase());
        if (!activeProj) {
            showToast("No active project found.");
            return;
        }

        hideDashboard();

        const icon = triggerScanBtn.querySelector('i') || triggerScanBtn.querySelector('svg');
        const textSpan = triggerScanBtn.querySelector('span');
        
        // Premium Visual Feedback: Spin icon, pulse button, change text
        if (icon) icon.classList.add('spin-animation');
        if (textSpan) textSpan.textContent = "Scanning...";
        triggerScanBtn.classList.add('scanning-pulse');
        triggerScanBtn.disabled = true;

        showToast(`Starting index scan for '${activeProj.name}'...`, 4000);

        try {
            const res = await fetch('/api/index', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ directory: activeProj.path })
            });

            if (res.ok) {
                const data = await res.json();
                
                showToast(`✅ Scan complete! Scanned ${data.result.filesScanned} files. Created ${data.result.nodesCreated} nodes.`, 6000);
                
                // Reload stats
                loadStats();
                
                // Refresh graph default workspace
                graphData = { nodes: [], links: [] }; if(network) network.graphData(graphData);
                
                allDiscoveredNodes.clear();
                loadDefaultGraph();
            } else {
                showToast("❌ Scanning failed.");
            }
        } catch (err) {
            console.error("Indexing failed:", err);
            showToast("❌ Error executing indexing scan.");
        } finally {
            // Restore original visual states
            if (icon) icon.classList.remove('spin-animation');
            if (textSpan) textSpan.textContent = "Scan Workspace";
            triggerScanBtn.classList.remove('scanning-pulse');
            triggerScanBtn.disabled = false;
        }
    }

    // TOAST HELPER
    let toastTimeout = null;
    function showToast(message, duration = 3000) {
        toastMessage.textContent = message;
        toast.classList.remove('hidden');

        if (toastTimeout) clearTimeout(toastTimeout);
        toastTimeout = setTimeout(() => {
            toast.classList.add('hidden');
        }, duration);
    }

    // DESIGN UTILS
    function getNodeBadgeClass(type) {
        switch (type.toLowerCase()) {
            case 'file': return 'badge-file';
            case 'class': case 'interface': case 'record': return 'badge-class';
            case 'method': return 'badge-method';
            case 'markdownsection': case 'sitecoretemplate': return 'badge-markdown';
            default: return 'badge-other';
        }
    }

    function getNodeColorAndStyle(type) {
        const typeLower = type.toLowerCase();
        
        const style = {
            color: {
                background: '#1d2433',
                border: '#64748b',
                highlight: { background: '#1d2433', border: '#06b6d4' },
                hover: { background: '#1e293b', border: '#94a3b8' }
            },
            size: 14
        };

        if (typeLower === 'file') {
            style.color.border = '#3b82f6';
            style.color.highlight.border = '#60a5fa';
            style.size = 20;
        } else if (['class', 'interface', 'record'].includes(typeLower)) {
            style.color.border = '#06b6d4';
            style.color.highlight.border = '#22d3ee';
            style.size = 18;
        } else if (typeLower === 'method') {
            style.color.border = '#a855f7';
            style.color.highlight.border = '#c084fc';
            style.size = 14;
        } else if (['markdownsection', 'concept'].includes(typeLower)) {
            style.color.border = '#10b981';
            style.color.highlight.border = '#34d399';
            style.size = 16;
        } else {
            // Dynamic color hash for unknown types
            let hash = 0;
            for (let i = 0; i < type.length; i++) {
                hash = type.charCodeAt(i) + ((hash << 5) - hash);
            }
            const hue = Math.abs(hash) % 360;
            style.color.border = `hsl(${hue}, 70%, 50%)`;
            style.color.highlight.border = `hsl(${hue}, 85%, 65%)`;
        }

        return style;
    }

    function getPrismLanguageClass(filePath) {
        const ext = filePath.split('.').pop().toLowerCase();
        switch (ext) {
            case 'cs': return 'language-csharp';
            case 'js': case 'jsx': return 'language-javascript';
            case 'ts': case 'tsx': return 'language-typescript';
            case 'php': return 'language-php';
            case 'md': return 'language-markdown';
            default: return 'language-none';
        }
    }

    function getRelativeFilePath(filePath) {
        if (!filePath) return "";
        const parts = filePath.split(/[\\/]/);
        if (parts.length > 3) {
            return ".../" + parts.slice(-3).join('/');
        }
        return filePath;
    }

    // === PROJECT MANAGER LOGIC ===

    // Fetch projects and active project on startup
    async function initProjects() {
        try {
            const res = await fetch('/api/projects');
            if (res.ok) {
                const data = await res.json();
                registeredProjects = data.projects || [];
                activeProject = data.activeProject || "Shonkor";
                
                // Update active project button name
                activeProjectName.textContent = activeProject;
                
                // Load node types first, then load stats and visual graph
                await initFilters();
                
                loadStats();
                loadDefaultGraph();
            }
        } catch (err) {
            console.error("Failed to initialize projects:", err);
            // Fallback load
            await initFilters();
            loadStats();
            loadDefaultGraph();
        }
    }

    // Load list of registered projects into left pane of the modal
    async function loadProjectsList() {
        try {
            const res = await fetch('/api/projects');
            if (res.ok) {
                const data = await res.json();
                registeredProjects = data.projects || [];
                activeProject = data.activeProject || "";
                activeProjectName.textContent = activeProject;
                
                renderProjectsList();
            }
        } catch (err) {
            console.error("Failed to load projects list:", err);
        }
    }

    // Render list of projects in left panel HTML
    function renderProjectsList() {
        projectListEl.innerHTML = "";
        
        const filteredProjects = registeredProjects.filter(proj => {
            const isActive = proj.name.toLowerCase() === activeProject.toLowerCase();
            
            // 1. Filter by active tab
            if (projectFilter === 'active' && !isActive) return false;
            
            // 2. Filter by search query (name or path)
            if (projectSearchQuery) {
                const query = projectSearchQuery.toLowerCase();
                const nameMatch = proj.name.toLowerCase().includes(query);
                const pathMatch = proj.path.toLowerCase().includes(query);
                if (!nameMatch && !pathMatch) return false;
            }
            
            return true;
        });

        filteredProjects.forEach(proj => {
            const isActive = proj.name.toLowerCase() === activeProject.toLowerCase();
            const li = document.createElement('li');
            li.className = `project-item ${isActive ? 'active' : ''}`;
            
            const iconClass = proj.name === activeProject ? 'lucide-check-circle' : 'lucide-folder';
            const iconColor = proj.name === activeProject ? 'var(--color-success)' : 'currentColor';
            li.innerHTML = `
                <i data-lucide="${iconClass}" style="color: ${iconColor};"></i>
                <span class="project-name">${escapeHtml(proj.name)}</span>
                ${proj.name === activeProject ? '<span class="active-badge">Active</span>' : ''}
                <span class="project-path" title="${escapeHtml(proj.path)}">${escapeHtml(proj.path)}</span>
            `;
            
            li.addEventListener('click', () => {
                selectProjectForConfig(proj);
                
                // Highlight item
                document.querySelectorAll('.project-item').forEach(item => item.classList.remove('active'));
                li.classList.add('active');
            });
            
            // Double click: activate project
            li.addEventListener('dblclick', (e) => {
                e.stopPropagation();
                switchProject(proj.name);
            });
            
            projectListEl.appendChild(li);
        });

        // Auto-select active project details on open
        if (filteredProjects.length > 0) {
            const activeProj = filteredProjects.find(p => p.name.toLowerCase() === activeProject.toLowerCase()) 
                               || filteredProjects[0];
            selectProjectForConfig(activeProj);
        }
    }

    // Select project and load its shonkor.json config in the right pane form
    async function selectProjectForConfig(proj) {
        selectedConfigProject = proj;
        configTitleEl.textContent = `Project Configuration: ${proj.name}`;
        
        // Show loading state in configuration fields
        configDbPathInput.value = "Loading...";
        configExclusionsTextarea.value = "Loading...";
        
        try {
            const res = await fetch(`/api/projects/${encodeURIComponent(proj.name)}/config`);
            if (res.ok) {
                const config = await res.json();
                configDbPathInput.value = config.databasePath || "shonkor.db";
                
                const exclusions = config.excludePatterns || [];
                configExclusionsTextarea.value = exclusions.join('\n');
            } else {
                showToast("Failed to retrieve project configuration.");
            }
        } catch (err) {
            console.error("Error fetching config:", err);
            showToast("Error loading project config.");
        }
    }

    // Switch the active project (switches DB and updates graph)
    async function switchProject(projectName) {
        if (!projectName || (activeProject && projectName.toLowerCase() === activeProject.toLowerCase())) return;
        
        showToast(`Switching active project to: ${projectName}...`);
        
        try {
            const res = await fetch('/api/projects/active', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ name: projectName })
            });

            if (res.ok) {
                activeProject = projectName;
                activeProjectName.textContent = projectName;
                
                // Close modal
                projectModal.classList.add('hidden');
                
                // Clear graph & drawer
                graphData = { nodes: [], links: [] }; if(network) network.graphData(graphData);
                
                allDiscoveredNodes.clear();
                hideDrawer();
                resultsListEl.innerHTML = "";
                resultsCountEl.textContent = "Project switched. Perform a search to load nodes.";
                globalSearchInput.value = "";
                
                showDashboard();
                loadDashboard();
                renderProjectsList();
                
                // Reload stats and visual index for the new active project
                await loadStats();
                await loadDefaultGraph();
                
                showToast(`Switched successfully to project '${projectName}'!`);
            } else {
                showToast("Failed to switch active project.");
            }
        } catch (err) {
            console.error("Error switching project:", err);
            showToast("Error: " + err.message);
        }
    }

    // Register a new project
    async function handleAddProject() {
        const name = newProjectNameInput.value.trim();
        const path = newProjectPathInput.value.trim();
        
        if (!name || !path) {
            showToast("Please enter both Name and absolute Path.");
            return;
        }

        showToast("Registering project...");
        addProjectBtn.disabled = true;

        try {
            const res = await fetch('/api/projects', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ name, path })
            });

            if (res.ok) {
                showToast(`Project '${name}' registered successfully!`, 4000);
                newProjectNameInput.value = "";
                newProjectPathInput.value = "";
                
                // Reload projects list & list rendered
                await loadProjectsList();
                
                // Automatically switch to the newly registered project
                await switchProject(name);
            } else {
                const err = await res.text();
                showToast(`Failed: ${err || 'Directory does not exist'}`);
            }
        } catch (err) {
            console.error("Error adding project:", err);
            showToast("Error registering new project.");
        } finally {
            addProjectBtn.disabled = false;
        }
    }

    // Save project shonkor.json config
    async function handleSaveConfig() {
        if (!selectedConfigProject) return;
        
        const projName = selectedConfigProject.name;
        const dbPath = configDbPathInput.value.trim();
        const exclusionsRaw = configExclusionsTextarea.value.trim();
        
        if (!dbPath) {
            showToast("Database Path is required.");
            return;
        }

        const excludePatterns = exclusionsRaw.split('\n')
                                             .map(p => p.trim())
                                             .filter(p => p.length > 0);

        showToast("Saving project configuration...");
        saveConfigBtn.disabled = true;

        try {
            const res = await fetch(`/api/projects/${encodeURIComponent(projName)}/config`, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ databasePath: dbPath, excludePatterns })
            });

            if (res.ok) {
                showToast("Configuration saved successfully!");
                await loadProjectsList(); // Refresh list to get updated database paths
            } else {
                showToast("Failed to save project configuration.");
            }
        } catch (err) {
            console.error("Error saving config:", err);
            showToast("Error saving project configuration.");
        } finally {
            saveConfigBtn.disabled = false;
        }
    }

    // Trigger selective scan on selected project (using its path & exclusions)
    async function handleScanProject() {
        if (!selectedConfigProject) return;
        
        const projName = selectedConfigProject.name;
        const projPath = selectedConfigProject.path;
        
        const icon = scanProjectBtn.querySelector('i') || scanProjectBtn.querySelector('svg');
        const textNode = scanProjectBtn.lastChild;
        
        // Premium Visual Feedback: Spin icon, pulse button, change text
        if (icon) icon.classList.add('spin-animation');
        if (textNode) textNode.textContent = " Indexing...";
        scanProjectBtn.classList.add('scanning-pulse');
        scanProjectBtn.disabled = true;

        showToast(`Starting indexer on project '${projName}'...`, 4000);

        try {
            // First, save current config on screen to ensure scan uses latest exclusions
            await handleSaveConfig();
            
            // Activate the project if it is not already active
            if (projName.toLowerCase() !== activeProject.toLowerCase()) {
                await switchProject(projName);
            }

            const res = await fetch('/api/index', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ directory: projPath })
            });

            if (res.ok) {
                const data = await res.json();
                showToast(`✅ Scan complete for '${projName}'! Scanned ${data.result.filesScanned} files. Created ${data.result.nodesCreated} nodes.`, 6000);
                
                // Hide modal and refresh graph/stats
                projectModal.classList.add('hidden');
                
                graphData = { nodes: [], links: [] }; if(network) network.graphData(graphData);
                
                allDiscoveredNodes.clear();
                hideDrawer();
                
                loadStats();
                loadDefaultGraph();
            } else {
                showToast("❌ Deregistered or invalid directories. Scanning failed.");
            }
        } catch (err) {
            console.error("Scanning failed:", err);
            showToast("❌ Error executing indexing scan.");
        } finally {
            // Restore original visual states
            if (icon) icon.classList.remove('spin-animation');
            if (textNode) textNode.textContent = " Index Project";
            scanProjectBtn.classList.remove('scanning-pulse');
            scanProjectBtn.disabled = false;
        }
    }

    // Deregister selected project
    async function handleDeleteProject() {
        if (!selectedConfigProject) return;
        const projName = selectedConfigProject.name;
        
        if (!confirm(`Are you sure you want to deregister project '${projName}' from the dashboard? (Your source files and database will NOT be deleted)`)) {
            return;
        }

        showToast("Deregistering project...");
        deleteProjectBtn.disabled = true;

        try {
            const res = await fetch(`/api/projects/${encodeURIComponent(projName)}`, {
                method: 'DELETE'
            });

            if (res.ok) {
                showToast(`Project '${projName}' deregistered successfully!`);
                selectedConfigProject = null;
                
                // Reload list
                await loadProjectsList();
                
                // If the deleted project was active, switch to whatever is active now
                const activeRes = await fetch('/api/projects');
                if (activeRes.ok) {
                    const activeData = await activeRes.json();
                    activeProject = activeData.activeProject || "";
                    activeProjectName.textContent = activeProject;
                    
                    // Clear and reload graph
                    graphData = { nodes: [], links: [] }; if(network) network.graphData(graphData);
                    
                    allDiscoveredNodes.clear();
                    hideDrawer();
                    resultsListEl.innerHTML = "";
                    
                    loadStats();
                    loadDefaultGraph();
                }
            } else {
                showToast("Failed to deregister project.");
            }
        } catch (err) {
            console.error("Deregistration failed:", err);
            showToast("Error deregistering project.");
        } finally {
            deleteProjectBtn.disabled = false;
        }
    }

    // Activate selected project
    async function handleActivateSelectedProject() {
        if (!selectedConfigProject) {
            showToast("Select a project first.");
            return;
        }
        await switchProject(selectedConfigProject.name);
    }

    // === FOLDER BROWSER DIALOG LOGIC ===

    // Open directory explorer and load path
    async function openFolderBrowser(initialPath) {
        browseModal.classList.remove('hidden');
        
        // Use initialPath, or fall back to empty to show logical drives
        await navigateFolderBrowser(initialPath);
    }

    // Fetch directory listing from backend and render
    async function navigateFolderBrowser(targetPath) {
        browseFoldersList.innerHTML = `<li style="font-size:0.8rem; padding:1rem; text-align:center; color:var(--color-text-muted);">Loading directory contents...</li>`;
        
        try {
            const url = `/api/browse?path=${encodeURIComponent(targetPath)}`;
            const res = await fetch(url);
            
            if (res.ok) {
                const data = await res.json();
                
                browseCurrentPath = data.currentPath || "";
                browseCurrentPathInput.value = browseCurrentPath;
                
                // If currentPath is empty, we are viewing the drives list
                if (!browseCurrentPath) {
                    browseSelectedPath = "";
                    browseSelectedPathLabel.textContent = "None";
                    
                    browseUpBtn.disabled = true;
                    browseUpBtn.dataset.parentPath = "";
                    
                    // Render logical drives
                    renderDrivesList(data.drives || []);
                    browseFoldersList.innerHTML = `<li style="font-size:0.8rem; padding:2rem; text-align:center; color:var(--color-text-muted); font-style:italic;">Select a drive from the left panel to begin browsing...</li>`;
                } else {
                    // Update parent navigation button state
                    if (data.parentPath) {
                        browseUpBtn.disabled = false;
                        browseUpBtn.dataset.parentPath = data.parentPath;
                    } else {
                        // We are at root, parent is empty (drives list view)
                        browseUpBtn.disabled = false;
                        browseUpBtn.dataset.parentPath = "";
                    }
                    
                    // Set selected path as the current directory by default
                    selectFolderForBrowser(browseCurrentPath);
                    
                    // Load and render folders list
                    renderFoldersList(data.folders || []);
                    
                    // Load logical drives list in sidebar in background if empty
                    if (browseDrivesList.children.length === 0) {
                        fetchDrivesInBackground();
                    }
                }
            } else {
                const errText = await res.text();
                showToast(`Failed: ${errText || 'Access denied or invalid path'}`);
                
                // If it fails, fallback to empty/drives view
                if (targetPath) {
                    await navigateFolderBrowser("");
                }
            }
        } catch (err) {
            console.error("Browse failed:", err);
            showToast("Failed to connect to directory browser API.");
        }
    }

    // Helper to fetch logical drives in background if we started browsing in a deep folder
    async function fetchDrivesInBackground() {
        try {
            const res = await fetch('/api/browse?path=');
            if (res.ok) {
                const data = await res.json();
                renderDrivesList(data.drives || []);
            }
        } catch (err) {
            // Silently ignore
        }
    }

    // Render logical drives in left side panel
    function renderDrivesList(drives) {
        browseDrivesList.innerHTML = "";
        
        drives.forEach(drive => {
            const li = document.createElement('li');
            li.className = 'browse-drive-item';
            li.innerHTML = `<i data-lucide="hard-drive"></i> <span>${drive}</span>`;
            
            li.addEventListener('click', () => {
                navigateFolderBrowser(drive);
                
                // Highlight item
                document.querySelectorAll('.browse-drive-item').forEach(item => item.classList.remove('selected'));
                li.classList.add('selected');
            });
            
            browseDrivesList.appendChild(li);
        });
        
        lucide.createIcons({
            attrs: { class: 'glow-icon-sm' },
            nameAttr: 'data-lucide',
            nodeList: browseDrivesList.querySelectorAll('[data-lucide]')
        });
    }

    // Render folders list in right side panel
    function renderFoldersList(folders) {
        browseFoldersList.innerHTML = "";
        
        if (folders.length === 0) {
            browseFoldersList.innerHTML = `<li style="font-size:0.8rem; padding:2rem; text-align:center; color:var(--color-text-muted); font-style:italic;">(This directory is empty or contains no subdirectories)</li>`;
            return;
        }

        folders.forEach(folder => {
            const fullPath = browseCurrentPath.endsWith('\\') || browseCurrentPath.endsWith('/')
                ? browseCurrentPath + folder
                : browseCurrentPath + '\\' + folder;
                
            const li = document.createElement('li');
            li.className = 'browse-folder-item';
            li.innerHTML = `<i data-lucide="folder"></i> <span class="folder-name">${folder}</span>`;
            
            // Single click: select folder
            li.addEventListener('click', (e) => {
                e.stopPropagation();
                selectFolderForBrowser(fullPath);
                
                document.querySelectorAll('.browse-folder-item').forEach(item => item.classList.remove('selected'));
                li.classList.add('selected');
            });
            
            // Double click: open folder
            li.addEventListener('dblclick', (e) => {
                e.stopPropagation();
                navigateFolderBrowser(fullPath);
            });
            
            browseFoldersList.appendChild(li);
        });
        
        lucide.createIcons({
            attrs: { class: 'glow-icon-sm' },
            nameAttr: 'data-lucide',
            nodeList: browseFoldersList.querySelectorAll('[data-lucide]')
        });
    }

    // Handle selecting a folder path in browser state
    function selectFolderForBrowser(path) {
        browseSelectedPath = path;
        browseSelectedPathLabel.textContent = path;
    }

    // ============================
    // PLUGIN MANAGEMENT FUNCTIONS
    // ============================

    // Load plugins list from API
    async function loadPluginsList() {
        if (!pluginListEl) return;
        
        pluginListEl.innerHTML = '';
        
        try {
            const res = await fetch('/api/plugins');
            if (!res.ok) {
                showToast('Failed to load plugins list.');
                return;
            }
            
            const data = await res.json();
            const plugins = data.plugins || [];
            
            if (plugins.length === 0) {
                pluginListEl.style.display = 'none';
                if (pluginEmptyState) pluginEmptyState.style.display = 'flex';
            } else {
                pluginListEl.style.display = 'flex';
                if (pluginEmptyState) pluginEmptyState.style.display = 'none';
                
                plugins.forEach(plugin => {
                    const li = document.createElement('li');
                    li.className = 'plugin-item';
                    
                    const statusClass = plugin.status === 'loaded' ? 'loaded' 
                                      : plugin.status === 'error' ? 'error' 
                                      : 'unknown';
                    const statusLabel = plugin.status === 'loaded' ? 'Loaded' 
                                      : plugin.status === 'error' ? 'Error' 
                                      : 'Pending';
                    
                    const sizeKb = plugin.sizeBytes ? (plugin.sizeBytes / 1024).toFixed(1) + ' KB' : '';
                    const modDate = plugin.lastModified 
                        ? new Date(plugin.lastModified).toLocaleDateString('de-DE', { day: '2-digit', month: '2-digit', year: 'numeric' }) 
                        : '';
                    
                    li.innerHTML = `
                        <div class="plugin-item-info">
                            <span class="plugin-item-name">${plugin.fileName}</span>
                            <div class="plugin-item-meta">
                                <span class="plugin-status-dot ${statusClass}" title="${statusLabel}${plugin.error ? ': ' + plugin.error : ''}"></span>
                                <span>${statusLabel}</span>
                                ${sizeKb ? `<span>${sizeKb}</span>` : ''}
                                ${modDate ? `<span>${modDate}</span>` : ''}
                            </div>
                        </div>
                        <div class="plugin-item-actions">
                            <button class="plugin-delete-btn" data-filename="${plugin.fileName}" title="Delete plugin">
                                <i data-lucide="trash-2"></i>
                            </button>
                        </div>
                    `;
                    
                    // Wire delete button
                    const deleteBtn = li.querySelector('.plugin-delete-btn');
                    deleteBtn.addEventListener('click', async (e) => {
                        e.stopPropagation();
                        await deletePlugin(plugin.fileName);
                    });
                    
                    pluginListEl.appendChild(li);
                });
            }
            
            if (window.lucide) window.lucide.createIcons();
            
        } catch (err) {
            console.error('Error loading plugins:', err);
            showToast('Error loading plugins list.');
        }
    }

    // Create a new plugin via the wizard
    async function handleCreatePlugin() {
        const name = pluginNameInput ? pluginNameInput.value.trim() : '';
        const ext = pluginExtensionInput ? pluginExtensionInput.value.trim() : '';
        
        if (!name || !ext) {
            showToast('Please enter both a plugin name and file extension.');
            return;
        }
        
        if (createPluginBtn) createPluginBtn.disabled = true;
        showToast('Creating plugin template...');
        
        try {
            const res = await fetch('/api/plugins/create', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ name, extension: ext })
            });
            
            if (res.ok) {
                const result = await res.json();
                showToast(`âœ¨ Plugin '${result.fileName}' created! Edit the file to customize your parser logic.`, 6000);
                
                // Clear inputs
                if (pluginNameInput) pluginNameInput.value = '';
                if (pluginExtensionInput) pluginExtensionInput.value = '';
                
                // Reload plugins list
                await loadPluginsList();
            } else {
                const errData = await res.json().catch(() => null);
                showToast(errData?.detail || errData?.message || 'Failed to create plugin template.');
            }
        } catch (err) {
            console.error('Error creating plugin:', err);
            showToast('Error creating plugin template.');
        } finally {
            if (createPluginBtn) createPluginBtn.disabled = false;
        }
    }

    // Delete a plugin file
    async function deletePlugin(fileName) {
        if (!confirm(`Delete plugin '${fileName}'? This action cannot be undone.`)) return;
        
        showToast(`Deleting plugin '${fileName}'...`);
        
        try {
            const res = await fetch(`/api/plugins/${encodeURIComponent(fileName)}`, {
                method: 'DELETE'
            });
            
            if (res.ok) {
                showToast(`Plugin '${fileName}' deleted.`);
                await loadPluginsList();
            } else {
                showToast('Failed to delete plugin.');
            }
        } catch (err) {
            console.error('Error deleting plugin:', err);
            showToast('Error deleting plugin.');
        }
    }

    // Wire up plugin create button
    if (createPluginBtn) {
        createPluginBtn.addEventListener('click', handleCreatePlugin);
    }
});
