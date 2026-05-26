// LLMBrain Dashboard Frontend Application Logic
// Uses vis.js for force-directed graph rendering and Prism.js for syntax highlighting

document.addEventListener('DOMContentLoaded', () => {
    // Initialize Lucide Icons
    lucide.createIcons();

    // DOM Elements
    const statsNodesEl = document.getElementById('stats-nodes');
    const statsEdgesEl = document.getElementById('stats-edges');
    const globalSearchInput = document.getElementById('global-search');
    const searchBtn = document.getElementById('search-btn');
    const resultsCountEl = document.getElementById('results-count');
    const resultsListEl = document.getElementById('search-results-list');
    const triggerScanBtn = document.getElementById('trigger-scan-btn');
    
    // Capsule synthesis DOM
    const capsuleQueryInput = document.getElementById('capsule-query');
    const capsuleHopsSelect = document.getElementById('capsule-hops');
    const generateCapsuleBtn = document.getElementById('generate-capsule-btn');
    
    // Graph control DOM
    const fitGraphBtn = document.getElementById('fit-graph-btn');
    const physicsGraphBtn = document.getElementById('physics-graph-btn');
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
    
    // Toast DOM
    const toast = document.getElementById('toast');
    const toastMessage = document.getElementById('toast-message');

    // Vis.js Network Variables
    let network = null;
    let nodesDataSet = new vis.DataSet([]);
    let edgesDataSet = new vis.DataSet([]);
    let allDiscoveredNodes = new Map(); // Keep track of node definitions by ID
    let currentSelectedNodeId = null;

    // Initialize Network Graph Viewport
    initNetwork();

    // Load initial stats & an initial visual index
    loadStats();
    loadDefaultGraph();

    // Event Listeners
    searchBtn.addEventListener('click', performSearch);
    globalSearchInput.addEventListener('keydown', (e) => {
        if (e.key === 'Enter') performSearch();
    });
    
    triggerScanBtn.addEventListener('click', triggerScan);
    generateCapsuleBtn.addEventListener('click', generateCapsule);
    
    // Graph View Controls
    fitGraphBtn.addEventListener('click', () => {
        if (network) network.fit({ animation: { duration: 1000 } });
    });
    
    physicsGraphBtn.addEventListener('click', () => {
        if (!network) return;
        const options = network.options;
        const isPhysicsActive = physicsGraphBtn.classList.contains('active');
        if (isPhysicsActive) {
            network.setOptions({ physics: { enabled: false } });
            physicsGraphBtn.classList.remove('active');
            showToast("Physics simulation paused.");
        } else {
            network.setOptions({ physics: { enabled: true } });
            physicsGraphBtn.classList.add('active');
            showToast("Physics simulation active.");
        }
    });

    clearGraphBtn.addEventListener('click', () => {
        nodesDataSet.clear();
        edgesDataSet.clear();
        allDiscoveredNodes.clear();
        hideDrawer();
        resultsCountEl.textContent = "Graph cleared. Perform a search to load nodes.";
        resultsListEl.innerHTML = "";
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

    // NETWORK INITIALIZATION
    function initNetwork() {
        const container = document.getElementById('network-graph');
        const data = {
            nodes: nodesDataSet,
            edges: edgesDataSet
        };
        
        const options = {
            nodes: {
                shape: 'dot',
                size: 16,
                font: {
                    size: 11,
                    color: '#e2e8f0',
                    face: 'Outfit'
                },
                borderWidth: 2,
                shadow: {
                    enabled: true,
                    color: 'rgba(0,0,0,0.5)',
                    size: 6,
                    x: 2,
                    y: 2
                }
            },
            edges: {
                width: 1.5,
                color: {
                    color: 'rgba(255, 255, 255, 0.15)',
                    highlight: '#06b6d4',
                    hover: 'rgba(255, 255, 255, 0.3)'
                },
                arrows: {
                    to: { enabled: true, scaleFactor: 0.8 }
                },
                smooth: {
                    type: 'continuous',
                    roundness: 0.5
                }
            },
            physics: {
                enabled: true,
                barnesHut: {
                    gravitationalConstant: -3000,
                    centralGravity: 0.3,
                    springLength: 95,
                    springConstant: 0.04,
                    damping: 0.09,
                    avoidOverlap: 0.5
                },
                stabilization: {
                    enabled: true,
                    iterations: 150,
                    updateInterval: 25
                }
            },
            interaction: {
                hover: true,
                tooltipDelay: 300
            }
        };

        network = new vis.Network(container, data, options);

        // Click Event -> Select Node / Show Drawer
        network.on("click", (params) => {
            if (params.nodes.length > 0) {
                const nodeId = params.nodes[0];
                selectNode(nodeId);
            }
        });

        // Double Click -> Expand Subgraph
        network.on("doubleClick", (params) => {
            if (params.nodes.length > 0) {
                const nodeId = params.nodes[0];
                expandSubgraph(nodeId);
            }
        });
    }

    // LOAD GRAPH DEFAULT STATE (Index files from current directory)
    async function loadDefaultGraph() {
        try {
            // Load some sample seeds if available. Let's do a search for empty/any to populate some visual elements on startup
            const res = await fetch('/api/search?q=Parser&limit=15');
            if (res.ok) {
                const data = await res.json();
                if (data.length > 0) {
                    displaySearchResults(data);
                    renderSearchInGraph(data);
                } else {
                    // Try to search Core or Infrastructure
                    const resFallback = await fetch('/api/search?q=Graph&limit=15');
                    if (resFallback.ok) {
                        const dataFallback = await resFallback.json();
                        displaySearchResults(dataFallback);
                        renderSearchInGraph(dataFallback);
                    }
                }
            }
        } catch (err) {
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
            }
        } catch (err) {
            console.error("Error loading statistics:", err);
        }
    }

    // PERFORM FTS5 GRAPH SEARCH
    async function performSearch() {
        const query = globalSearchInput.value.trim();
        if (!query) {
            showToast("Search query cannot be empty.");
            return;
        }

        resultsCountEl.textContent = "Searching...";
        resultsListEl.innerHTML = "";

        try {
            const res = await fetch(`/api/search?q=${encodeURIComponent(query)}&limit=20`);
            if (res.ok) {
                const results = await res.json();
                resultsCountEl.textContent = `Found ${results.length} matched node(s):`;
                displaySearchResults(results);
                renderSearchInGraph(results);
            } else {
                resultsCountEl.textContent = "Search failed.";
                showToast("Search failed. Verify query syntax.");
            }
        } catch (err) {
            resultsCountEl.textContent = "Search error.";
            console.error("Search failed:", err);
        }
    }

    // DISPLAY SIDEBAR RESULTS
    function displaySearchResults(results) {
        resultsListEl.innerHTML = "";
        
        results.forEach(res => {
            const node = res.Node;
            allDiscoveredNodes.set(node.Id, node); // Add to cache

            const li = document.createElement('li');
            li.className = 'result-item';
            
            const badgeClass = getNodeBadgeClass(node.Type);
            const relativePath = getRelativeFilePath(node.FilePath);
            
            li.innerHTML = `
                <div class="result-item-header">
                    <div class="node-name" title="${node.Name}">${node.Name}</div>
                    <span class="node-type-badge ${badgeClass}">${node.Type}</span>
                </div>
                <div class="node-path" title="${node.FilePath}">${relativePath}</div>
            `;

            li.addEventListener('click', () => {
                // Focus in Graph
                if (network) {
                    network.selectNodes([node.Id]);
                    network.focus(node.Id, {
                        scale: 1.1,
                        animation: { duration: 800 }
                    });
                }
                selectNode(node.Id);
            });

            resultsListEl.appendChild(li);
        });
    }

    // RENDER NODES AND EDGES IN VIS GRAPH CONTAINER
    function renderSearchInGraph(searchResults) {
        // Collect node IDs to fetch their N-hop relations to construct a beautiful connected web
        const seedNodeIds = searchResults.map(r => r.Node.Id);
        if (seedNodeIds.length === 0) return;

        // Fetch direct N-hops connection mapping
        fetchSubgraph(seedNodeIds, 1);
    }

    // FETCH SUBGRAPH AND MERGE TO CANVAS DATASET
    async function fetchSubgraph(seedIds, hops = 1) {
        try {
            const res = await fetch(`/api/subgraph?seeds=${encodeURIComponent(seedIds.join(','))}&hops=${hops}`);
            if (res.ok) {
                const data = await res.json();
                mergeGraphData(data.nodes, data.edges);
            }
        } catch (err) {
            console.error("Failed to fetch connected subgraph:", err);
        }
    }

    // DOUBLE CLICK -> EXPAND FROM NODE
    function expandSubgraph(nodeId) {
        showToast(`Expanding graph around: ${allDiscoveredNodes.get(nodeId)?.Name || nodeId}`);
        fetchSubgraph([nodeId], 1);
    }

    // MERGE VIS NETWORK DATA (Aesthetic visual designs for nodes by type)
    function mergeGraphData(nodes, edges) {
        const currentNodes = nodesDataSet.getIds();
        const currentEdges = edgesDataSet.getIds();
        
        const newNodes = [];
        const newEdges = [];

        nodes.forEach(node => {
            allDiscoveredNodes.set(node.Id, node); // Keep cache up to date

            if (!currentNodes.includes(node.Id)) {
                // Assign beautiful color themes & icon glows based on type
                const style = getNodeColorAndStyle(node.Type);
                
                newNodes.push({
                    id: node.Id,
                    label: node.Name,
                    color: style.color,
                    size: style.size,
                    title: `Type: ${node.Type}\nPath: ${getRelativeFilePath(node.FilePath)}`
                });
            }
        });

        edges.forEach(edge => {
            const edgeId = `${edge.SourceId}-${edge.TargetId}-${edge.Relationship}`;
            if (!currentEdges.includes(edgeId)) {
                newEdges.push({
                    id: edgeId,
                    from: edge.SourceId,
                    to: edge.TargetId,
                    label: edge.Relationship,
                    font: { size: 8, color: '#94a3b8', strokeWidth: 0, face: 'JetBrains Mono' }
                });
            }
        });

        if (newNodes.length > 0) nodesDataSet.add(newNodes);
        if (newEdges.length > 0) edgesDataSet.add(newEdges);

        if (network && (newNodes.length > 0 || newEdges.length > 0)) {
            // Relayout and scale nicely
            network.stabilize();
            network.fit({ animation: { duration: 1000 } });
        }
    }

    // DETAILED SELECTION DRAWER
    async function selectNode(nodeId) {
        currentSelectedNodeId = nodeId;
        const node = allDiscoveredNodes.get(nodeId);
        if (!node) return;

        // Render Details
        drawerNodeType.textContent = node.Type;
        drawerNodeType.className = `type-badge ${getNodeBadgeClass(node.Type)}`;
        drawerNodeName.textContent = node.Name;
        drawerNodePath.textContent = node.FilePath || "Virtual Namespace Node";

        // Properties section
        drawerNodeProperties.innerHTML = "";
        const properties = node.Properties || {};
        
        let hasProps = false;
        for (const [key, value] of Object.entries(properties)) {
            // Skip core fields already printed
            if (["Content", "FilePath", "StartLine", "EndLine", "ContentHash"].includes(key)) {
                continue;
            }
            hasProps = true;
            const propEl = document.createElement('div');
            propEl.className = 'prop-pill';
            propEl.innerHTML = `<span class="prop-key">${key}</span><span class="prop-val">${value}</span>`;
            drawerNodeProperties.appendChild(propEl);
        }

        const propsSec = document.getElementById('drawer-properties-section');
        if (hasProps) {
            propsSec.style.display = 'flex';
        } else {
            propsSec.style.display = 'none';
        }

        // Connections & Relations
        drawerNodeRelations.innerHTML = "";
        const connectedEdges = edgesDataSet.get().filter(e => e.from === nodeId || e.to === nodeId);
        
        if (connectedEdges.length === 0) {
            drawerNodeRelations.innerHTML = `<li style="font-size:0.75rem; color:var(--color-text-muted); font-style:italic; padding: 0.2rem 0.5rem;">No local relations displayed...</li>`;
        } else {
            connectedEdges.forEach(e => {
                const isSource = e.from === nodeId;
                const partnerId = isSource ? e.to : e.from;
                const relationship = e.label || "Connected";
                const direction = isSource ? "→" : "←";
                
                const partnerNode = allDiscoveredNodes.get(partnerId);
                const partnerName = partnerNode ? partnerNode.Name : partnerId.split('/').pop().split('\\').pop();

                const li = document.createElement('li');
                li.className = 'relation-link';
                li.innerHTML = `
                    <span class="rel-target" title="${partnerId}">${partnerName}</span>
                    <span class="rel-type">${direction} ${relationship}</span>
                `;
                li.addEventListener('click', () => {
                    if (network) {
                        network.selectNodes([partnerId]);
                        network.focus(partnerId, { animation: { duration: 500 } });
                    }
                    selectNode(partnerId);
                });
                drawerNodeRelations.appendChild(li);
            });
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
        
        Prism.highlightElement(codeElement);

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
        if (network) network.unselectNodes();
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
        showToast("Starting workspace index scan...", 4000);
        triggerScanBtn.disabled = true;

        try {
            const res = await fetch('/api/index', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ directory: "." })
            });

            if (res.ok) {
                const data = await res.json();
                
                showToast(`Scan complete! Scanned ${data.result.filesScanned} files. Created ${data.result.nodesCreated} nodes.`, 5000);
                
                // Reload stats
                loadStats();
                
                // Refresh graph default workspace
                nodesDataSet.clear();
                edgesDataSet.clear();
                allDiscoveredNodes.clear();
                loadDefaultGraph();
            } else {
                showToast("Scanning failed.");
            }
        } catch (err) {
            console.error("Indexing failed:", err);
            showToast("Error executing indexing scan.");
        } finally {
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
});
