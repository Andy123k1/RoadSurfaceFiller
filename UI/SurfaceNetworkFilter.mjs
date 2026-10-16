/*
 * Cities: Skylines II UI Module
 * Id: SurfaceNetworkFilter
 * Author: Road Surface Filler
 * Version: 0.5.23
 */

// The game's shared React instance and documented modding hook are used
// directly. No second React runtime, script injection or UI dependency.
const React = window.React;
const { createElement: h, useState, useEffect, useLayoutEffect, useMemo, useRef, useCallback } = React;
const { createPortal } = window.ReactDOM;
const { bindValue, useValue, trigger } = window["cs2/api"];
const { Button, Panel, Tooltip, Scrollable } = window["cs2/ui"];
const { InputActionConsumer } = window["cs2/input"];
const group = "SurfaceNetworkFilter";
const openBinding = bindValue(group, "open", false);
const stateBinding = bindValue(group, "state", "{}");
const roadToolBinding = bindValue(group, "roadToolActive", false);
const send = (action, ...args) => trigger(group, action, ...args);
export const hasCSS = true;

const targets = [
    { value: 0, label: "Road pavement" },
    { value: 1, label: "Sidewalks" },
    { value: 3, label: "Bike lanes" },
    { value: 4, label: "Bus lanes" }
];
const categories = [
    { value: 0, label: "All roads" },
    { value: 1, label: "Streets" },
    { value: 2, label: "Highways and ramps" },
    { value: 3, label: "Small streets (up to 16 m)" },
    { value: 4, label: "Medium streets (16–32 m)" },
    { value: 5, label: "Large streets (over 32 m)" }
];
const junctionEnds = [
    { value: 0, label: "Nearest junction" },
    { value: 1, label: "Other end of road" }
];
const labelOf = (items, value) => items.find(item => item.value === value)?.label || "Choose…";
const emptyDraft = () => ({ id: "", name: "", enabled: true, category: 0, roadPrefab: "",
    assignments: [{ target: 0, surface: "" }], separateBikeLanes: false, separateBusLanes: false, fillBusShoulders: false });
const laneTargetActive = (rule, target) => target === 3 ? !!(rule.separateBikeLanes ?? rule.inheritedBikeLanes) :
    target === 4 ? !!(rule.separateBusLanes ?? rule.inheritedBusLanes) : true;
const segmentLaneModes = [{ value: null, label: "Use existing lane settings" }, { value: false, label: "Use road pavement" },
    { value: true, label: "Separate lane surface" }];
let savedPanelPosition = null;
let savedPanelSize = null;
let expandedRuleIds = new Set();

function MovablePanel({ header, onClose, children, compact = false }) {
    const boundary = useRef(null);
    const interaction = useRef(null);
    const compactRef = useRef(compact);
    compactRef.current = compact;
    const [position, setPosition] = useState(savedPanelPosition);
    const [size, setSize] = useState(savedPanelSize);
    const sizeRef = useRef(savedPanelSize);
    const [activity, setActivity] = useState("");
    const metrics = () => {
        if (!boundary.current) return null;
        const rect = boundary.current.getBoundingClientRect();
        const content = boundary.current.querySelector(".snf-panel-content");
        const rem = parseFloat(window.getComputedStyle(document.documentElement).fontSize) || 1;
        return { rect, rem, chrome: content ? Math.max(0, rect.height - content.getBoundingClientRect().height) : 0 };
    };
    const updatePosition = point => {
        savedPanelPosition = point;
        setPosition(previous => previous && Math.abs(previous.x - point.x) < 0.5 && Math.abs(previous.y - point.y) < 0.5 ? previous : point);
    };
    const updateSize = next => {
        sizeRef.current = next;
        savedPanelSize = next;
        setSize(previous => previous && Math.abs(previous.width - next.width) < 0.5 && Math.abs(previous.height - next.height) < 0.5 &&
            Math.abs(previous.chrome - next.chrome) < 0.5 ? previous : next);
    };
    const clampSize = (width, height, left, top, info) => {
        const maxWidth = Math.max(1, window.innerWidth - left - 8), maxHeight = Math.max(1, window.innerHeight - top - 8);
        return { width: Math.max(Math.min(480 * info.rem, maxWidth), Math.min(width, maxWidth)),
            height: Math.max(Math.min(430 * info.rem, maxHeight), Math.min(height, maxHeight)), chrome: info.chrome };
    };
    const fitToScreen = () => {
        const info = metrics();
        if (!info) return;
        const { rect } = info, maxWidth = Math.max(1, window.innerWidth - 16), maxHeight = Math.max(1, window.innerHeight - 16);
        let width = rect.width, height = rect.height;
        if (!compactRef.current && (sizeRef.current || width > maxWidth || height > maxHeight)) {
            const requested = sizeRef.current || rect;
            const next = clampSize(requested.width, requested.height, 8, 8, info);
            width = next.width; height = next.height; updateSize(next);
        }
        updatePosition({ x: Math.max(8, Math.min(rect.left, window.innerWidth - width - 8)),
            y: Math.max(8, Math.min(rect.top, window.innerHeight - height - 8)) });
    };
    useEffect(() => {
        const move = event => {
            const active = interaction.current;
            if (!active) return;
            event.preventDefault(); event.stopPropagation();
            if (active.kind === "resize") {
                updateSize(clampSize(active.width + event.clientX - active.x, active.height + event.clientY - active.y,
                    active.left, active.top, active));
            } else {
                const info = metrics();
                if (!info) return;
                updatePosition({ x: Math.max(8, Math.min(event.clientX - active.x, window.innerWidth - info.rect.width - 8)),
                    y: Math.max(8, Math.min(event.clientY - active.y, window.innerHeight - info.rect.height - 8)) });
            }
        };
        const release = event => {
            if (!interaction.current) return;
            event.preventDefault(); event.stopPropagation(); interaction.current = null; setActivity("");
        };
        const blur = () => { interaction.current = null; setActivity(""); };
        const resize = () => { blur(); fitToScreen(); };
        // Native panels can stop bubbling mouse events before window sees
        // them. Capture active drags before those handlers run.
        window.addEventListener("mousemove", move, true);
        window.addEventListener("mouseup", release, true);
        window.addEventListener("resize", resize);
        window.addEventListener("blur", blur);
        return () => {
            window.removeEventListener("mousemove", move, true);
            window.removeEventListener("mouseup", release, true);
            window.removeEventListener("resize", resize);
            window.removeEventListener("blur", blur);
        };
    }, []);
    const startDrag = event => {
        if (event.button !== 0) return;
        const rect = boundary.current.getBoundingClientRect();
        interaction.current = { kind: "move", x: event.clientX - rect.left, y: event.clientY - rect.top };
        event.preventDefault(); event.stopPropagation(); setActivity("move");
    };
    const startResize = event => {
        if (event.button !== 0 || compactRef.current) return;
        const info = metrics();
        if (!info) return;
        event.preventDefault(); event.stopPropagation();
        const { rect } = info;
        updatePosition({ x: rect.left, y: rect.top });
        interaction.current = { ...info, kind: "resize", x: event.clientX, y: event.clientY,
            left: rect.left, top: rect.top, width: rect.width, height: rect.height };
        updateSize({ width: rect.width, height: rect.height, chrome: info.chrome }); setActivity("resize");
    };
    const resetSize = () => { interaction.current = null; sizeRef.current = savedPanelSize = null; setSize(null); setActivity(""); };
    const resizeWithKeys = event => {
        if (event.ctrlKey || event.altKey || event.metaKey) return;
        if (event.key === "Home") { event.preventDefault(); event.stopPropagation(); resetSize(); return; }
        if (!["ArrowLeft", "ArrowRight", "ArrowUp", "ArrowDown"].includes(event.key)) return;
        const info = metrics();
        if (!info) return;
        event.preventDefault(); event.stopPropagation();
        const step = (event.shiftKey ? 40 : 10) * info.rem;
        updateSize(clampSize(info.rect.width + (event.key === "ArrowRight" ? step : event.key === "ArrowLeft" ? -step : 0),
            info.rect.height + (event.key === "ArrowDown" ? step : event.key === "ArrowUp" ? -step : 0), info.rect.left, info.rect.top, info));
    };
    useLayoutEffect(() => { if (!interaction.current) fitToScreen(); }, [children, compact, size]);
    const style = position ? { left: position.x + "px", top: position.y + "px" } : {};
    if (size && !compact) style.width = size.width + "px";
    // Cohtml does not resolve the var(name, fallback) expressions used by
    // the first resizer. Apply ordinary dimensions directly to our elements.
    const contentHeight = size && !compact ? Math.max(1, size.height - size.chrome) + "px" : null;
    const content = contentHeight ? React.cloneElement(children, {
        style: { ...children.props.style, height: contentHeight, maxHeight: contentHeight }
    }) : children;
    return h("div", { ref: boundary, className: "snf-movable" + (compact ? " snf-movable-compact" : "") +
        (activity === "move" ? " snf-dragging" : activity === "resize" ? " snf-resizing" : ""), style },
        h(Panel, { className: "snf-panel" + (compact ? " snf-panel-compact" : ""), onClose, allowFocusExit: true,
            header: h("div", { className: "snf-drag-handle", onMouseDown: startDrag, title: "Drag to move" }, header) }, content,
        !compact && h("button", { type: "button", className: "snf-resize-handle", "aria-label": "Resize menu",
            title: "Drag to resize. Arrow keys adjust size; double-click or Home resets.",
            onMouseDown: startResize, onDoubleClick: event => { event.preventDefault(); event.stopPropagation(); resetSize(); }, onKeyDown: resizeWithKeys })));
}

function Checkbox({ label, checked, disabled, onChange }) {
    return h("button", { type: "button", role: "checkbox", "aria-checked": checked, disabled,
        className: "snf-checkbox-row", onClick: () => onChange(!checked) },
        h("span", { className: "snf-checkbox" + (checked ? " snf-checkbox-selected" : ""), "aria-hidden": true }, checked ? "✓" : ""), label);
}

function ScrollRegion({ children, ...props }) {
    const boundary = useRef(null);
    useEffect(() => {
        const element = boundary.current;
        // The native Scrollable handles wheel input on its content element.
        // Stop it at this outer boundary after that handler has run, so a
        // dropdown does not also scroll the editor or zoom the city.
        const stopWheel = event => event.stopPropagation();
        element.addEventListener("wheel", stopWheel);
        return () => element.removeEventListener("wheel", stopWheel);
    }, []);
    return h("div", { ...props, ref: boundary },
        h(Scrollable, { className: "snf-scroll", vertical: true, horizontal: false,
            trackVisibility: "scrollable" }, children));
}

function Picker({ id, value, items, onChange, placeholder = "Choose…", searchable = false, disabled = false, floating = false }) {
    const [expanded, setExpanded] = useState(false);
    const [query, setQuery] = useState("");
    const anchor = useRef(null), menu = useRef(null);
    const [position, setPosition] = useState(null);
    const selected = items.find(item => item.value === value);
    const found = useMemo(() => {
        const search = query.toLowerCase().trim();
        return items.filter(item => !search || item.label.toLowerCase().includes(search));
    }, [items, query]);
    useLayoutEffect(() => {
        if (!expanded || !floating || !anchor.current || !menu.current) return;
        const place = () => {
            if (!anchor.current || !menu.current) return;
            const rect = anchor.current.getBoundingClientRect(), height = menu.current.getBoundingClientRect().height;
            const width = Math.min(Math.max(rect.width, 210), window.innerWidth - 16);
            setPosition({ left: Math.max(8, Math.min(rect.left, window.innerWidth - width - 8)), width,
                top: Math.max(8, rect.bottom + height + 4 < window.innerHeight - 8 ? rect.bottom + 4 : rect.top - height - 4) });
        };
        const outside = event => {
            if (!anchor.current?.contains(event.target) && !menu.current?.contains(event.target)) setExpanded(false);
        };
        place(); window.addEventListener("resize", place); document.addEventListener("mousedown", outside);
        return () => { window.removeEventListener("resize", place); document.removeEventListener("mousedown", outside); };
    }, [expanded, floating, query, found.length]);
    const choices = expanded && !disabled && h("div", { ref: menu,
        className: "snf-picker-menu" + (floating ? " snf-floating-picker" : ""),
        style: floating ? { ...position, visibility: position ? "visible" : "hidden" } : undefined,
        onKeyDown: event => { if (event.key === "Escape") { event.preventDefault(); event.stopPropagation(); setExpanded(false); } } },
        searchable && h("input", {
            type: "text", className: "snf-input snf-picker-search", value: query,
            placeholder: "Search…", "aria-label": "Search choices", autoFocus: true,
            onChange: event => setQuery(event.target.value),
            onKeyDown: event => {
                if (event.key === "Enter" && found.length === 1) {
                    event.preventDefault(); onChange(found[0].value); setExpanded(false);
                }
            }
        }),
        h(ScrollRegion, { className: "snf-picker-items", role: "listbox", "aria-labelledby": id,
            style: { height: Math.min(165, Math.max(38, found.length * 36)) + "rem" } },
            found.length === 0 && h("div", { className: "snf-picker-empty" }, "No matching choices"),
            found.map(item => h("button", {
                key: String(item.value), type: "button", role: "option", "aria-selected": item.value === value,
                className: "snf-picker-item" + (item.value === value ? " snf-picker-item-selected" : ""),
                onClick: () => { onChange(item.value); setExpanded(false); }
            }, item.label))));
    return h("div", { ref: anchor, className: "snf-picker" },
        h("button", {
            id, type: "button", className: "snf-picker-toggle", disabled,
            "aria-expanded": expanded, "aria-haspopup": "listbox",
            onClick: () => { setExpanded(!expanded); setQuery(""); }
        }, h("span", { className: selected ? "" : "snf-muted" }, selected?.label || value || placeholder),
            h("span", { className: "snf-chevron" + (expanded ? " snf-chevron-up" : ""), "aria-hidden": true })),
        floating ? choices && createPortal(choices, document.body) : choices
    );
}

function Field({ label, id, hint, children }) {
    return h("div", { className: "snf-field" }, h("label", { htmlFor: id }, label), children,
        hint && h("div", { className: "snf-field-hint" }, hint));
}

function Toggle({ label, checked, onChange, disabled = false }) {
    return h("div", { className: "snf-check-row" },
        h("button", { type: "button", role: "switch", "aria-checked": checked, "aria-label": label,
            className: "snf-switch" + (checked ? " snf-switch-on" : ""), disabled,
            onClick: () => onChange(!checked) }, h("span")),
        h("span", { className: "snf-toggle-label" }, label));
}

function JunctionEditor({ junction = {}, saving }) {
    const busy = saving || junction.pending;
    return h("div", { className: "snf-junction-editor" },
        h("div", { className: "snf-junction-controls" },
            h("span", { className: "snf-eyebrow" }, "JUNCTION SURFACE"),
            h("div", { className: "snf-junction-end" },
                h(Picker, { id: "snf-junction-end", value: junction.end || 0, items: junctionEnds,
                    disabled: busy, onChange: value => send("junctionEnd", value) })),
            h("button", { type: "button", className: "snf-action snf-action-small",
                disabled: busy || !junction.available || !junction.canUseSelected,
                onClick: () => send("junctionUseSelected") }, "Use selected road"),
            h("button", { type: "button", className: "snf-action snf-action-small",
                disabled: busy || !junction.available || !junction.hasOverride,
                onClick: () => send("junctionAutomatic") }, "Restore automatic")),
        h("div", { className: "snf-junction-source", role: "status" },
            junction.available ? h(React.Fragment, null,
                h("div", null, h("strong", null, junction.kind + " pavement: "), junction.source),
                h("div", { className: "snf-junction-surface" }, junction.surface ||
                    (junction.canCycle ? "Cycle to choose a connected road's surface." : "No connected road has an active pavement rule."))) :
                "Select a connected road with the Info tool near the junction or roundabout, then click Cycle junction surface."));
}

function RuleEditor({ draft, setDraft, surfaces, roads = [], onSave, onCancel, saving, local = false, applyDisabled = false }) {
    const change = (key, value) => setDraft(current => ({ ...current, [key]: value }));
    const surfaceItems = useMemo(() => surfaces.map(name => ({ value: name, label: name })), [surfaces]);
    const roadItems = useMemo(() => [{ value: "", label: "All road types in this category" },
        ...roads.map(name => ({ value: name, label: name }))], [roads]);
    const assignments = draft.assignments || [];
    const hasOptions = local ? assignments.length > 0 || draft.separateBikeLanes !== null || draft.separateBusLanes !== null || draft.fillBusShoulders :
        assignments.length > 0 || draft.separateBikeLanes || draft.separateBusLanes;
    const canSave = !saving && !applyDisabled && hasOptions &&
        (local || !draft.fillBusShoulders || !!assignments.find(item => item.target === 4)?.surface) &&
        assignments.every(item => (local || item.target >= 3 || !!item.surface) &&
        (!draft.enabled || (!local && !laneTargetActive(draft, item.target)) || !item.surface || surfaces.includes(item.surface)));
    const selectTarget = (target, selected) => setDraft(current => ({ ...current,
        assignments: selected ? [...current.assignments, { target, surface: "" }].sort((a, b) => a.target - b.target) :
            current.assignments.filter(item => item.target !== target),
        separateBikeLanes: target === 3 && selected ? true : current.separateBikeLanes,
        separateBusLanes: target === 4 && selected ? true : current.separateBusLanes,
        fillBusShoulders: target === 4 && !selected && !local ? false : current.fillBusShoulders }));
    const setSurface = (target, surface) => setDraft(current => ({ ...current,
        assignments: current.assignments.map(item => item.target === target ? { ...item, surface } : item) }));
    const shoulderControl = h("div", { className: "snf-shoulder-control" },
        h(Toggle, { label: "Fill paved shoulders with bus-lane surface", checked: !!draft.fillBusShoulders, disabled: saving,
            onChange: value => setDraft(current => ({ ...current, fillBusShoulders: value,
                separateBusLanes: value ? true : current.separateBusLanes,
                assignments: value && !local && !current.assignments.some(item => item.target === 4) ?
                    [...current.assignments, { target: 4, surface: "" }].sort((a, b) => a.target - b.target) : current.assignments })) }),
        h("div", { className: "snf-field-hint" }, local ?
            "Extends the segment’s bus-lane surface to adjacent paved shoulders. Junction shoulders connect when both roads enable this option. Grass, sidewalks and other traffic lanes stay protected." :
            "Choose a bus-lane surface below to fill adjacent paved shoulders on matching roads. Junction shoulders connect when both roads enable this option. Grass, sidewalks, bike lanes and other traffic lanes stay protected."));
    return h("div", { className: "snf-editor" },
        h("div", { className: "snf-editor-heading" },
            h("span", { className: "snf-eyebrow" }, local ? draft.id ? "EDIT SEGMENT RULE" : "NEW SEGMENT RULE" : draft.id ? "EDIT RULE" : "NEW RULE"),
            h("h3", null, draft.name || "Choose where to apply a surface")),
        h(ScrollRegion, { className: "snf-editor-fields" },
            h("div", { className: "snf-editor-field-content" },
            h(Field, { label: "Rule name", id: "snf-rule-name" },
                h("input", { id: "snf-rule-name", className: "snf-input", type: "text", maxLength: 120,
                    value: draft.name, placeholder: local ? "For example, bus street with paved shoulders" : "For example, cobblestone alleys", disabled: saving,
                    onChange: event => change("name", event.target.value) })),
            local && h("div", { className: "snf-rule-lane-options" },
                h(Toggle, { label: "Update placed roads when this preset is saved", checked: !!draft.updateExistingSegments, disabled: saving,
                    onChange: value => change("updateExistingSegments", value) }),
                h("div", { className: "snf-field-hint" }, "On: saving changes updates roads using this preset. Off: placed roads keep their current finish until you reapply it with Replace. Independent shoulder-tool customizations stay separate.")),
            !local && h(Field, { label: "Road category", id: "snf-rule-category" },
                h(Picker, { id: "snf-rule-category", value: draft.category, items: categories, disabled: saving,
                    onChange: value => setDraft(current => ({ ...current, category: value, roadPrefab: "" })) })),
            !local && h(Field, { label: "Exact road type", id: "snf-rule-prefab", hint: "Selecting an exact type applies this rule only to that road type." },
                h(Picker, { id: "snf-rule-prefab", value: draft.roadPrefab, items: roadItems, searchable: true,
                    disabled: saving, onChange: value => setDraft(current => ({ ...current, roadPrefab: value,
                        category: value ? 0 : current.category })) })),
            h("fieldset", { className: "snf-targets" }, h("legend", null, "Apply surface to"),
                h("div", { className: "snf-target-checks" }, targets.map(target => h(Checkbox, { key: target.value,
                    label: target.label, checked: assignments.some(item => item.target === target.value), disabled: saving,
                    onChange: selected => selectTarget(target.value, selected) })))),
            local ? h("div", { className: "snf-rule-lane-options" },
                h(Field, { label: "Bike lane handling", id: "snf-local-bike-mode" },
                    h(Picker, { id: "snf-local-bike-mode", value: draft.separateBikeLanes, items: segmentLaneModes, disabled: saving,
                        onChange: value => change("separateBikeLanes", value) })),
                h(Field, { label: "Bus lane handling", id: "snf-local-bus-mode" },
                    h(Picker, { id: "snf-local-bus-mode", value: draft.separateBusLanes, items: segmentLaneModes, disabled: saving,
                        onChange: value => setDraft(current => ({ ...current, separateBusLanes: value,
                            fillBusShoulders: value === false ? false : current.fillBusShoulders })) })),
                shoulderControl) :
            h("div", { className: "snf-rule-lane-options" },
                h(Toggle, { label: "Separate bike lane surfaces", checked: !!draft.separateBikeLanes, disabled: saving,
                    onChange: value => change("separateBikeLanes", value) }),
                h(Toggle, { label: "Separate bus lane surfaces", checked: !!draft.separateBusLanes, disabled: saving,
                    onChange: value => setDraft(current => ({ ...current, separateBusLanes: value,
                        fillBusShoulders: value ? current.fillBusShoulders : false })) }),
                h("div", { className: "snf-field-hint" }, "For this rule’s roads: off uses road pavement. On keeps the original lane finish until you choose a lane surface."),
                shoulderControl),
            assignments.map(item => {
                const lane = item.target >= 3;
                const active = laneTargetActive(draft, item.target);
                const requiresBusSurface = !local && draft.fillBusShoulders && item.target === 4;
                const id = "snf-rule-surface-" + item.target;
                return h("section", { key: item.target, className: "snf-target-surface" },
                    h(Field, { label: labelOf(targets, item.target) + " surface", id,
                        hint: requiresBusSurface && !item.surface ? "Choose a surface to fill bus lanes and their paved shoulders." :
                            !active ? "Separation is off. These lanes use road pavement." : (lane || local) && !item.surface ? "Original finish will remain visible." : null },
                        h(Picker, { id, value: item.surface, items: (lane || local) && !requiresBusSurface ? [{ value: "", label: "Original finish" }, ...surfaceItems] : surfaceItems,
                            searchable: true, placeholder: "Choose an installed surface", disabled: saving || !active || (!lane && !local && !surfaces.length),
                            onChange: value => setSurface(item.target, value) }),
                        active && item.surface && !surfaces.includes(item.surface) &&
                            h("div", { className: "snf-warning" }, "Surface unavailable. Choose another surface or disable this rule.")));
            }),
            !local && h(Toggle, { label: "Enable this rule", checked: draft.enabled, disabled: saving,
                onChange: value => change("enabled", value) }),
            h("div", { className: "snf-note" }, local ? "General rules are optional. Unchecked targets inherit existing city finishes or keep vanilla finishes. Choose this preset in the road tool when drawing or using Replace. " +
                (draft.updateExistingSegments ? "Saving also updates roads linked to this preset." : "Saving keeps placed roads as they are.") :
                "Exact road types take priority over categories, then all roads. Lane settings use the most specific matching rules; when those rules overlap, any enabled separation switch keeps that lane separate."))),
        h("div", { className: "snf-editor-actions" },
            h("button", { type: "button", className: "snf-action", disabled: saving, onClick: onCancel }, "Cancel"),
            h("button", { type: "button", className: "snf-action snf-action-primary", disabled: !canSave, onClick: onSave },
                saving ? "Saving…" : draft.id ? "Save changes" : local ? "Create segment rule" : "Create rule")));
}

function UpgradeSelection({ upgrade }) {
    return h("div", { className: "snf-upgrade-selection" },
        h("span", { className: "snf-eyebrow" }, "ROAD TOOL"),
        h("h3", null, upgrade.label),
        h("p", null, "Click or drag over roads to select several segments. Click a selected road again to remove it."),
        h("div", { className: "snf-selection-count", role: "status" }, upgrade.count + " segments selected"),
        h("div", { className: "snf-tool-buttons" },
            h("button", { type: "button", className: "snf-action", onClick: () => send("cancelUpgrade") }, "Cancel"),
            h("button", { type: "button", className: "snf-action", disabled: !upgrade.count, onClick: () => send("clearUpgradeSelection") }, "Clear selection"),
            h("button", { type: "button", className: "snf-action snf-action-primary", disabled: !upgrade.count,
                onClick: () => send("applyUpgrade") }, "Apply changes")),
        h("div", { className: "snf-field-hint" }, "Changes apply only after Apply changes. Right-click or Escape cancels."));
}

function UpgradeTools({ state }) {
    const [busSurface, setBusSurface] = useState("");
    const [confirmRestoreAll, setConfirmRestoreAll] = useState(false);
    const start = mode => send("startUpgrade", JSON.stringify({ mode, busSurface: mode === 1 ? busSurface : "" }));
    return h(ScrollRegion, { className: "snf-tools-scroll" }, h("div", { className: "snf-tools-content" },
        state.upgrade?.message && h("div", { className: "snf-message", role: "status" }, state.upgrade.message),
        h("section", { className: "snf-tool-card" },
            h("h3", null, "Fill bus-lane shoulders"),
            h("p", null, "Extend the bus-lane finish across its adjacent paved shoulder. Existing segment adjustments are kept."),
            h(Field, { label: "Bus-lane surface", id: "snf-shoulder-surface" },
                h(Picker, { id: "snf-shoulder-surface", value: busSurface, searchable: true, onChange: setBusSurface,
                    items: [{ value: "", label: "Use each road's current bus-lane surface" },
                        ...(state.surfaces || []).map(name => ({ value: name, label: name }))] })),
            h("button", { type: "button", className: "snf-action snf-action-primary", onClick: () => start(1) }, "Select road segments"),
            h("div", { className: "snf-field-hint" }, "Shoulders connect through junctions and around turns when both connected roads have shoulder filling enabled. Choose a surface here if the road has no custom bus finish. Grass, trees, sidewalks and bike lanes stay protected."),
            h("div", { className: "snf-shoulder-remove" },
                h("button", { type: "button", className: "snf-action", onClick: () => start(3) }, "Remove bus-lane shoulder fill"),
                h("div", { className: "snf-field-hint" }, "Select roads, then Apply changes to turn off their shoulder fill, including connections around turns and through junctions. Bus-lane surfaces and other finishes are kept. No surface selection is needed."))),
        h("section", { className: "snf-tool-card" },
            h("h3", null, "Restore vanilla surfaces"),
            h("p", null, "Remove this mod's city and segment surface finishes from selected road segments."),
            h("button", { type: "button", className: "snf-action", onClick: () => start(2) }, "Select segments to restore"),
            h("div", { className: "snf-field-hint" }, "Click or drag to select roads, then Apply changes. They stay vanilla until you apply a preset or Apply city rules again. Connected roads keep their saved finishes."),
            h("div", { className: "snf-restore-all" },
                h("button", { type: "button", className: "snf-action", disabled: state.restoringAllRoads,
                    onClick: () => setConfirmRestoreAll(!confirmRestoreAll) }, state.restoringAllRoads ? "Restoring all roads…" : "Restore all roads to vanilla"),
                confirmRestoreAll && !state.restoringAllRoads && h("div", { className: "snf-restore-confirm", role: "group", "aria-label": "Confirm restoring all roads" },
                    h("p", null, "Restore every road and junction to vanilla? Your saved rules and presets will stay available."),
                    h("div", { className: "snf-tool-buttons" },
                        h("button", { type: "button", className: "snf-action", onClick: () => setConfirmRestoreAll(false) }, "Cancel"),
                        h("button", { type: "button", className: "snf-action snf-action-danger",
                            onClick: () => { setConfirmRestoreAll(false); send("restoreAllRoads"); } }, "Restore all roads"))))),
        h("section", { className: "snf-tool-card snf-junction-tool" },
            h("h3", null, "Junction surface"),
            h("button", { type: "button", className: "snf-action", disabled: !state.junction?.canCycle || state.junction?.pending,
                onClick: () => send("junctionNextRoad") }, "Cycle junction surface"),
            h("div", { className: "snf-field-hint" }, "At bus-lane junctions, cycle between pavement with continuing bus lanes, pavement with bus lanes stopped at crosswalks, and a bus-surface junction fill. Bike surfaces stay visible."),
            h(JunctionEditor, { junction: state.junction || {}, saving: false }))));
}

function RuleCards({ rules, local, onEdit, saving, onCopyToSegments }) {
    const [deleteId, setDeleteId] = useState("");
    const [expanded, setExpanded] = useState(() => new Set(expandedRuleIds));
    const updateExpansion = next => { expandedRuleIds = next; setExpanded(next); };
    const toggleRule = id => {
        const next = new Set(expanded);
        if (next.has(id)) next.delete(id); else next.add(id);
        updateExpansion(next);
    };
    const visibleIds = new Set(rules.map(rule => rule.id));
    return h(React.Fragment, null,
        rules.length > 1 && h("div", { className: "snf-list-display-actions" },
            h("button", { type: "button", className: "snf-action snf-action-small", disabled: rules.every(rule => expanded.has(rule.id)),
                onClick: () => updateExpansion(new Set([...expanded, ...visibleIds])) }, "Expand all"),
            h("button", { type: "button", className: "snf-action snf-action-small", disabled: !rules.some(rule => expanded.has(rule.id)),
                onClick: () => updateExpansion(new Set([...expanded].filter(id => !visibleIds.has(id)))) }, "Collapse all")),
        h("div", { className: "snf-card-grid" }, rules.map(rule => {
        const open = expanded.has(rule.id);
        const detailsId = "snf-rule-details-" + rule.id;
        const summary = [local ? "Selected segments" : rule.roadPrefab || labelOf(categories, rule.category),
            (rule.assignments || []).length ? rule.assignments.map(item => labelOf(targets, item.target)).join(", ") : "Lane settings only",
            rule.fillBusShoulders ? "Bus-lane shoulders" : "",
            local ? rule.updateExistingSegments ? "Updates placed roads" : "Keeps placed finishes" : ""].filter(Boolean).join(" · ");
        return h("section", { key: rule.id, className: "snf-rule" + (!rule.enabled ? " snf-rule-disabled" : "") + (open ? "" : " snf-rule-collapsed") },
            h("div", { className: "snf-rule-top" },
                h("button", { type: "button", className: "snf-rule-name", "aria-expanded": open, "aria-controls": detailsId,
                    title: open ? "Collapse rule" : "Expand rule", onClick: () => toggleRule(rule.id) },
                    h("span", { className: "snf-rule-chevron" + (open ? " snf-rule-chevron-open" : ""), "aria-hidden": true }),
                    h("span", { className: "snf-rule-title" }, rule.name)),
                !local && h("button", { type: "button", role: "switch", "aria-checked": rule.enabled,
                    "aria-label": (rule.enabled ? "Disable " : "Enable ") + rule.name,
                    className: "snf-switch" + (rule.enabled ? " snf-switch-on" : ""), disabled: saving,
                    onClick: () => send("enableRule", rule.id, !rule.enabled) }, h("span"))),
            !open && h("div", { className: "snf-rule-summary" }, summary),
            open && h("div", { id: detailsId },
            (rule.assignments || []).map(item => h("div", { key: item.target, className: "snf-rule-surface" },
                h("strong", null, labelOf(targets, item.target) + ": "),
                local && ((item.target === 3 && rule.separateBikeLanes === null) || (item.target === 4 && rule.separateBusLanes === null)) ?
                    item.surface || "Original finish" : laneTargetActive(rule, item.target) ? item.surface || "Original finish" : "Road pavement")),
            h("div", { className: "snf-rule-tags" },
                !local && h("span", null, rule.roadPrefab || labelOf(categories, rule.category)),
                rule.separateBikeLanes && h("span", null, "Separate bike lanes"),
                rule.separateBusLanes && h("span", null, "Separate bus lanes"),
                rule.fillBusShoulders && h("span", null, "Bus-lane shoulders"),
                local && h("span", null, rule.updateExistingSegments ? "Updates placed roads" : "Keeps placed finishes"),
                local && h("span", null, "Use in road tool"))),
            !rule.available && h("div", { className: "snf-warning" }, "Surface unavailable"),
            h("div", { className: "snf-rule-actions" },
                h("button", { type: "button", onClick: () => onEdit(rule), disabled: saving }, "Edit"),
                h("button", { type: "button", disabled: saving,
                    onClick: () => onEdit({ ...rule, id: "", name: rule.name + " (copy)" }) }, "Duplicate"),
                !local && h("button", { type: "button", disabled: saving,
                    onClick: () => { send("copyRuleToSegments", rule.id); onCopyToSegments(); } }, "Copy to Segment rules"),
                h("button", { type: "button", className: "snf-delete", disabled: saving,
                    onClick: () => setDeleteId(deleteId === rule.id ? "" : rule.id) }, "Delete")),
            deleteId === rule.id && h("div", { className: "snf-delete-confirm" },
                h("span", null, local ? "Delete segment rule? Applied roads keep their surfaces." : "Delete this rule?"),
                h("button", { type: "button", onClick: () => setDeleteId("") }, "Cancel"),
                h("button", { type: "button", onClick: () => { send(local ? "deleteSegmentPreset" : "deleteRule", rule.id); setDeleteId(""); } }, "Delete")));
        })));
}

function RuleManager({ state, onClose }) {
    const [draft, setDraft] = useState(null);
    const [tab, setTab] = useState("rules");
    const [query, setQuery] = useState("");
    const [saving, setSaving] = useState(false);
    const pending = useRef(null);
    useEffect(() => {
        if (state.editorRequest) { setTab("segments"); setDraft(null); setQuery(""); }
    }, [state.editorRequest]);
    const local = tab === "segments";
    const rules = local ? state.segmentPresets || [] : state.rules || [];
    const upgrade = state.upgrade || {};
    const active = (state.rules || []).filter(rule => rule.enabled && rule.available).length;
    const closeActions = useMemo(() => ({ "Pause Menu": onClose, Close: onClose, Back: onClose }), [onClose]);
    useEffect(() => {
        if (pending.current !== null && state.saveResponse !== pending.current) {
            pending.current = null; setSaving(false);
            if (state.savedRuleId) setDraft(null);
        }
    }, [state.saveResponse, state.savedRuleId]);
    const edit = rule => setDraft({ ...rule, assignments: (rule.assignments || []).map(item => ({ ...item })) });
    const changeTab = value => { setTab(value); setDraft(null); setQuery(""); };
    const createSegmentRule = () => {
        setTab("segments"); setQuery("");
        setDraft({ ...emptyDraft(), separateBikeLanes: null, separateBusLanes: null, fillBusShoulders: false, updateExistingSegments: false });
    };
    const add = () => {
        if (local) { createSegmentRule(); return; }
        if (tab === "tools") setTab("rules");
        setDraft(emptyDraft());
    };
    const save = () => {
        pending.current = state.saveResponse || 0; setSaving(true);
        send(local ? "saveSegmentPreset" : "saveRule", JSON.stringify(local ? { rule: draft, fillBusShoulders: !!draft.fillBusShoulders,
            updateExistingSegments: !!draft.updateExistingSegments } : draft));
    };
    const filtered = rules.filter(rule => [rule.name, rule.roadPrefab || "", ...(rule.assignments || []).map(item => item.surface)]
        .join(" ").toLowerCase().includes(query.toLowerCase().trim()));
    const header = h("div", { className: "snf-header" },
        h("img", { className: "snf-header-icon", src: "coui://ui-mods/SurfaceNetworkFilter.svg", alt: "" }),
        h("div", { className: "snf-title" }, "Road Surface Filler",
            h("span", null, upgrade.active ? "Select road segments" : draft ? "Rule editor" : "Rules and tools")));
    return h(InputActionConsumer, { actions: closeActions, ignoreFocusState: true },
        h(MovablePanel, { header, onClose, compact: !!upgrade.active },
            upgrade.active ? h(UpgradeSelection, { upgrade }) :
            h("div", { className: "snf-panel-content" },
                h("div", { className: "snf-tabs", role: "tablist", "aria-label": "Road Surface Filler" },
                    [["rules", "General rules"], ["segments", "Segment rules"], ["tools", "Tools"]].map(([value, label]) =>
                        h("button", { key: value, type: "button", role: "tab", "aria-selected": tab === value, disabled: saving,
                            className: "snf-tab" + (tab === value ? " snf-tab-selected" : ""), onClick: () => changeTab(value) }, label))),
                tab !== "tools" && !draft && h("div", { className: "snf-toolbar" },
                    h("div", { className: "snf-count" }, rules.length + (local ? " segment rules" : " general rules"),
                        h("span", null, local ? "Use while drawing roads or with Replace" : active + " enabled · apply changes when ready")),
                    h("div", { className: "snf-toolbar-actions" },
                        !local && h("button", { type: "button", className: "snf-action snf-action-apply",
                            disabled: saving || state.restoringAllRoads || (!rules.length && !state.appliedCityRuleCount),
                            onClick: () => send("applyCityRules") }, "Apply city rules"),
                        h("button", { type: "button", className: "snf-action snf-action-primary", onClick: add }, local ? "+ New segment rule" : "+ New rule"))),
                h("div", { className: "snf-layout" },
                    tab === "tools" ? h(UpgradeTools, { state }) :
                    draft ? h(RuleEditor, { draft, setDraft, surfaces: state.surfaces || [], roads: state.roadTypes || [],
                        local, saving, onSave: save, onCancel: () => setDraft(null) }) :
                    h("div", { className: "snf-list-column" },
                        h("div", { className: "snf-section-note" }, local ?
                            "Choose a preset when drawing or using Replace. Each preset can update placed roads when saved, or keep their applied finishes." :
                            "Save rules here, then choose Apply city rules to update existing roads. Editing this list does not repaint the city. Segment overrides take priority."),
                        rules.length > 0 && h("input", { className: "snf-input snf-rule-search", value: query, type: "text",
                            placeholder: "Search rules…", "aria-label": "Search surface rules", onChange: event => setQuery(event.target.value) }),
                        h(ScrollRegion, { className: "snf-rule-list" },
                            rules.length === 0 ? h("div", { className: "snf-empty" },
                                h("h3", null, local ? "Customize selected roads" : "Your roads, your surfaces"),
                                h("p", null, local ? "Create a segment rule with its own surfaces and lane settings. Choose it in the road tool when drawing or using Replace. You can keep General rules empty." :
                                    "Create a rule with separate finishes for pavement, sidewalks and dedicated lanes."),
                                h("button", { type: "button", className: "snf-action snf-action-primary", onClick: add }, local ? "Create segment rule" : "Create rule")) :
                            filtered.length === 0 ? h("div", { className: "snf-empty" }, "No rules match your search.") :
                            h(RuleCards, { rules: filtered, local, saving, onEdit: edit, onCopyToSegments: () => changeTab("segments") })))),
                h("div", { className: "snf-footer" },
                    (state.message || state.cityMessage) && h("div", { className: "snf-message", role: "status" }, state.message || state.cityMessage),
                    tab === "rules" && !draft && h("div", { className: "snf-city-apply-status" },
                        state.cityRulesPending ? "Library changes have not been applied to this city." : "City surfaces use the last applied rules."),
                    h("div", { className: "snf-footer-row" },
                        h("div", { className: "snf-footer-buttons" },
                            h("button", { type: "button", className: "snf-action snf-action-small", onClick: () => send("refreshCatalog") }, "Refresh surfaces"),
                            h("button", { type: "button", className: "snf-action snf-action-small",
                                disabled: !state.appliedCityRuleCount && !state.activeSegmentRuleCount, onClick: () => send("rebuild") }, "Rebuild applied surfaces"))),
                    h("div", { className: "snf-status" }, state.status || "Loading surface catalog…")))));
}

function SurfaceNetworkButton() {
    const open = useValue(openBinding);
    const json = useValue(stateBinding);
    const state = useMemo(() => { try { return JSON.parse(json || "{}"); } catch { return {}; } }, [json]);
    const closePanel = useCallback(() => { send("closePanel"); }, []);
    return h(React.Fragment, null,
        h("div", { className: "snf-toolbar-entry" },
            h(Tooltip, { tooltip: "Road Surface Filler — create and manage surface rules", direction: "right" },
                h(Button, { className: "snf-toolbar-button", variant: "floating", tinted: true,
                    src: "coui://ui-mods/SurfaceNetworkFilter.svg", selected: open,
                    onSelect: () => send("togglePanel"), "aria-label": "Road Surface Filler", "aria-expanded": open }))),
        open && createPortal(h(RuleManager, { state, onClose: closePanel }), document.body));
}

export default function register(registry) {
    registry.append("GameTopLeft", SurfaceNetworkButton);
    // Use the game's exported component so the picker occupies a real row in
    // the road options panel and participates in its layout/height tracking.
    registry.extend("game-ui/game/components/tool-options/mouse-tool-options/mouse-tool-options.tsx",
        "MouseToolOptions", Original => props => h(React.Fragment, null,
            h(Original, props), h(NewRoadSurfacePicker)));
}

function NewRoadSurfacePicker() {
    const active = useValue(roadToolBinding);
    const json = useValue(stateBinding);
    const state = useMemo(() => { try { return JSON.parse(json || "{}"); } catch { return {}; } }, [json]);
    const items = useMemo(() => [{ value: "", label: "None (vanilla)" },
        ...(state.segmentPresets || []).filter(preset => preset.available).map(preset => ({ value: preset.id, label: preset.name }))], [state.segmentPresets]);
    if (!active) return null;
    return h("div", { className: "snf-road-option" },
        h("div", { className: "snf-road-option-label" }, "Surface rule"),
        h("div", { className: "snf-road-option-content" },
            h(Picker, { id: "snf-new-road-preset", value: state.newRoadPresetId || "", items,
                floating: true, searchable: items.length > 8, onChange: value => send("newRoadPreset", value) }),
            h("div", { className: "snf-road-option-help" }, state.roadToolReplace ?
                "Replace: click or drag to apply this preset, even on the same road type. None restores vanilla surfaces." :
                "Draw with this preset. Connected roads keep their surfaces. Use Replace to restyle existing roads."),
            h("button", { type: "button", className: "snf-action snf-action-small", onClick: () => send("openSegmentPresets") }, "Edit presets")));
}
