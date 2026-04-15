window.planningGraphInterop = (() => {
    let nextId = 1;
    const registrations = new Map();

    const isEditableTarget = (target) => {
        if (!(target instanceof Element)) {
            return false;
        }

        return Boolean(target.closest("input, textarea, select, [contenteditable='true'], [contenteditable=''], .mud-input-slot"));
    };

    const isGraphActive = (registration) => {
        const activeElement = document.activeElement;
        if (!(activeElement instanceof Element)) {
            return false;
        }

        return activeElement === registration.element || registration.element.contains(activeElement);
    };

    const isDisposedReferenceError = (error) => {
        const text = error?.message ?? String(error ?? "");
        return text.includes("There is no tracked object with id")
            || text.includes("Cannot find .NET object reference");
    };

    const invoke = (registration, methodName, ...args) => {
        if (!registration || registration.disposed || !registration.dotNet) {
            return;
        }

        registration.dotNet.invokeMethodAsync(methodName, ...args).catch((error) => {
            if (isDisposedReferenceError(error)) {
                registration.disposed = true;
                registration.pendingMove = null;
                return;
            }

            console.warn("planningGraphInterop", methodName, error);
        });
    };

    const notifySpaceState = (registration, isPressed) => {
        if (!registration || registration.disposed || !registration.dotNet || registration.spacePressed === isPressed) {
            return;
        }

        registration.spacePressed = isPressed;
        registration.dotNet.invokeMethodAsync("SetSpacePressed", isPressed).catch((error) => {
            if (isDisposedReferenceError(error)) {
                registration.disposed = true;
                registration.pendingMove = null;
            }
        });
    };

    const getNodeId = (target) => {
        if (!(target instanceof Element)) {
            return null;
        }

        const node = target.closest(".diagram-node[data-node-id]");
        return node instanceof HTMLElement ? node.dataset.nodeId ?? null : null;
    };

    const getLinkId = (target) => {
        if (!(target instanceof Element)) {
            return null;
        }

        const link = target.closest(".diagram-link[data-link-id]");
        return link instanceof SVGGElement || link instanceof HTMLElement
            ? link.dataset.linkId ?? null
            : null;
    };

    return {
        register(element, dotNet) {
            if (!element) {
                return null;
            }

            const id = `planning-graph-${nextId++}`;
            const registration = {
                element,
                dotNet,
                disposed: false,
                spacePressed: false,
                pendingMove: null,
                moveScheduled: false
            };

            registration.onKeyDown = (event) => {
                if (event.code === "Space" && isGraphActive(registration) && !isEditableTarget(event.target)) {
                    event.preventDefault();
                    notifySpaceState(registration, true);
                }
            };

            registration.onKeyUp = (event) => {
                if (event.code === "Space" && isGraphActive(registration) && !isEditableTarget(event.target)) {
                    event.preventDefault();
                    notifySpaceState(registration, false);
                }
            };

            registration.onBlur = () => notifySpaceState(registration, false);
            registration.flushMove = () => {
                if (registration.disposed) {
                    registration.moveScheduled = false;
                    registration.pendingMove = null;
                    return;
                }

                registration.moveScheduled = false;
                const pendingMove = registration.pendingMove;
                registration.pendingMove = null;
                if (!pendingMove) {
                    return;
                }

                invoke(registration, "HandleHostPointerMove", pendingMove.clientX, pendingMove.clientY);
            };

            registration.onPointerDown = (event) => {
                element.focus?.({ preventScroll: true });

                if (event.button !== 0 || event.target instanceof Element && event.target.closest(".planning-graph-toolbar")) {
                    return;
                }

                const nodeId = getNodeId(event.target);
                if (nodeId) {
                    invoke(registration, "HandleNodePointerDown", nodeId, event.clientX, event.clientY, event.button);
                    return;
                }

                if (getLinkId(event.target)) {
                    return;
                }

                invoke(registration, "HandleHostPointerDown", event.clientX, event.clientY, event.button);
            };

            registration.onPointerMove = (event) => {
                registration.pendingMove = {
                    clientX: event.clientX,
                    clientY: event.clientY
                };

                if (registration.moveScheduled) {
                    return;
                }

                registration.moveScheduled = true;
                window.requestAnimationFrame(registration.flushMove);
            };

            registration.onPointerUp = (event) => {
                invoke(registration, "HandleHostPointerUp", event.clientX, event.clientY, event.button);
            };

            registration.onPointerCancel = () => {
                invoke(registration, "HandleHostPointerCancel");
            };

            registration.onClick = (event) => {
                const nodeId = getNodeId(event.target);
                if (nodeId) {
                    invoke(registration, "HandleNodeClick", nodeId);
                    return;
                }

                const linkId = getLinkId(event.target);
                if (linkId) {
                    invoke(registration, "HandleLinkClick", linkId);
                }
            };

            registration.onDoubleClick = (event) => {
                const nodeId = getNodeId(event.target);
                if (nodeId) {
                    event.preventDefault();
                    invoke(registration, "HandleNodeDoubleClick", nodeId);
                    return;
                }
            };

            registration.onWheel = (event) => {
                if (event.ctrlKey) {
                    event.preventDefault();
                    invoke(registration, "HandleWheel", event.clientX, event.clientY, event.deltaY, true);
                }
            };

            window.addEventListener("keydown", registration.onKeyDown, true);
            window.addEventListener("keyup", registration.onKeyUp, true);
            window.addEventListener("blur", registration.onBlur, true);
            element.addEventListener("pointerdown", registration.onPointerDown, true);
            element.addEventListener("pointermove", registration.onPointerMove, true);
            element.addEventListener("pointerup", registration.onPointerUp, true);
            element.addEventListener("pointercancel", registration.onPointerCancel, true);
            element.addEventListener("click", registration.onClick, true);
            element.addEventListener("dblclick", registration.onDoubleClick, true);
            element.addEventListener("wheel", registration.onWheel, { passive: false });

            registrations.set(id, registration);
            return id;
        },

        refreshRegistration(element, dotNet, currentId) {
            if (!element) {
                return null;
            }

            if (currentId) {
                const current = registrations.get(currentId);
                if (current?.element === element) {
                    current.disposed = false;
                    current.dotNet = dotNet;
                    return currentId;
                }

                this.unregister(currentId);
            }

            return this.register(element, dotNet);
        },

        unregister(id) {
            const registration = registrations.get(id);
            if (!registration) {
                return;
            }

            registration.disposed = true;
            registration.pendingMove = null;

            window.removeEventListener("keydown", registration.onKeyDown, true);
            window.removeEventListener("keyup", registration.onKeyUp, true);
            window.removeEventListener("blur", registration.onBlur, true);

            if (registration.element) {
                registration.element.removeEventListener("pointerdown", registration.onPointerDown, true);
                registration.element.removeEventListener("pointermove", registration.onPointerMove, true);
                registration.element.removeEventListener("pointerup", registration.onPointerUp, true);
                registration.element.removeEventListener("pointercancel", registration.onPointerCancel, true);
                registration.element.removeEventListener("click", registration.onClick, true);
                registration.element.removeEventListener("dblclick", registration.onDoubleClick, true);
                registration.element.removeEventListener("wheel", registration.onWheel, { passive: false });
            }

            registrations.delete(id);
        }
    };
})();

window.runtimeWorkflowGraphInterop = (() => {
    let nextId = 1;
    const registrations = new Map();
    const nodeDragActivationDistance = 4;

    const isDisposedReferenceError = (error) => {
        const text = error?.message ?? String(error ?? "");
        return text.includes("There is no tracked object with id")
            || text.includes("Cannot find .NET object reference");
    };

    const invoke = (registration, methodName, ...args) => {
        if (!registration || registration.disposed || !registration.dotNet) {
            return;
        }

        registration.dotNet.invokeMethodAsync(methodName, ...args).catch((error) => {
            if (isDisposedReferenceError(error)) {
                registration.disposed = true;
                return;
            }

            console.warn("runtimeWorkflowGraphInterop", methodName, error);
        });
    };

    const isPanGestureTarget = (target) => {
        if (!(target instanceof Element)) {
            return false;
        }

        return !target.closest(".runtime-workflow-toolbar")
            && !target.closest(".runtime-workflow-details-shell")
            && !target.closest(".runtime-workflow-node")
            && !target.closest(".runtime-workflow-link-hit");
    };

    const getNodeId = (target) => {
        if (!(target instanceof Element)) {
            return null;
        }

        const node = target.closest(".runtime-workflow-node[data-node-id]");
        return node instanceof HTMLElement ? node.dataset.nodeId ?? null : null;
    };

    return {
        register(element, dotNet) {
            if (!element) {
                return null;
            }

            const id = `runtime-workflow-graph-${nextId++}`;
            const registration = {
                element,
                dotNet,
                disposed: false,
                draggingNodeId: null,
                draggingNodeMoved: false,
                dragStartClientX: 0,
                dragStartClientY: 0,
                panning: false,
                pendingMove: null,
                moveScheduled: false,
                suppressedNodeClickId: null
            };

            registration.flushPendingMove = () => {
                const pendingMove = registration.pendingMove;
                registration.pendingMove = null;
                if (!pendingMove) {
                    return;
                }

                if (pendingMove.kind === "node") {
                    invoke(registration, "HandleNodePointerMove", pendingMove.nodeId, pendingMove.clientX, pendingMove.clientY);
                    return;
                }

                invoke(registration, "HandleViewportPointerMove", pendingMove.clientX, pendingMove.clientY);
            };

            registration.flushMove = () => {
                if (registration.disposed) {
                    registration.moveScheduled = false;
                    registration.pendingMove = null;
                    return;
                }

                registration.moveScheduled = false;
                registration.flushPendingMove();
            };

            registration.onPointerDown = (event) => {
                if (event.button !== 0) {
                    return;
                }

                const nodeId = getNodeId(event.target);
                if (nodeId) {
                    event.preventDefault();
                    element.focus?.({ preventScroll: true });
                    registration.draggingNodeId = nodeId;
                    registration.draggingNodeMoved = false;
                    registration.dragStartClientX = event.clientX;
                    registration.dragStartClientY = event.clientY;
                    registration.pendingMove = null;
                    invoke(registration, "HandleNodePointerDown", nodeId, event.clientX, event.clientY);
                    return;
                }

                if (!isPanGestureTarget(event.target)) {
                    return;
                }

                event.preventDefault();
                registration.panning = true;
                element.focus?.({ preventScroll: true });
                invoke(registration, "HandleViewportPointerDown", event.clientX, event.clientY);
            };

            registration.onPointerMove = (event) => {
                if (registration.draggingNodeId) {
                    const deltaX = event.clientX - registration.dragStartClientX;
                    const deltaY = event.clientY - registration.dragStartClientY;

                    if (!registration.draggingNodeMoved && Math.hypot(deltaX, deltaY) < nodeDragActivationDistance) {
                        return;
                    }

                    event.preventDefault();
                    registration.draggingNodeMoved = true;
                    registration.pendingMove = {
                        kind: "node",
                        nodeId: registration.draggingNodeId,
                        clientX: event.clientX,
                        clientY: event.clientY
                    };

                    if (registration.moveScheduled) {
                        return;
                    }

                    registration.moveScheduled = true;
                    window.requestAnimationFrame(registration.flushMove);
                    return;
                }

                if (!registration.panning) {
                    return;
                }

                event.preventDefault();
                registration.pendingMove = {
                    kind: "pan",
                    clientX: event.clientX,
                    clientY: event.clientY
                };

                if (registration.moveScheduled) {
                    return;
                }

                registration.moveScheduled = true;
                window.requestAnimationFrame(registration.flushMove);
            };

            registration.endNodeDrag = (cancelled) => {
                if (!registration.draggingNodeId) {
                    return;
                }

                registration.flushPendingMove();
                const nodeId = registration.draggingNodeId;
                const moved = registration.draggingNodeMoved;
                registration.draggingNodeId = null;
                registration.draggingNodeMoved = false;

                if (cancelled) {
                    invoke(registration, "HandleNodePointerCancel", nodeId);
                }
                else {
                    invoke(registration, "HandleNodePointerUp", nodeId);
                }

                registration.suppressedNodeClickId = moved ? nodeId : null;
            };

            registration.endPan = () => {
                if (!registration.panning) {
                    return;
                }

                registration.flushPendingMove();
                registration.panning = false;
                invoke(registration, "HandleViewportPointerUp");
            };

            registration.onPointerUp = (event) => {
                if (registration.draggingNodeId) {
                    event.preventDefault();
                    registration.endNodeDrag(false);
                    return;
                }

                if (!registration.panning) {
                    return;
                }

                event.preventDefault();
                registration.endPan();
            };

            registration.onPointerCancel = () => {
                if (registration.draggingNodeId) {
                    registration.endNodeDrag(true);
                    return;
                }

                if (!registration.panning) {
                    return;
                }

                registration.panning = false;
                registration.pendingMove = null;
                invoke(registration, "HandleViewportPointerCancel");
            };

            registration.onClick = (event) => {
                const nodeId = getNodeId(event.target);
                if (!nodeId) {
                    registration.suppressedNodeClickId = null;
                    return;
                }

                if (registration.suppressedNodeClickId === nodeId) {
                    registration.suppressedNodeClickId = null;
                    event.preventDefault();
                    event.stopPropagation();
                    return;
                }

                registration.suppressedNodeClickId = null;
            };

            element.addEventListener("pointerdown", registration.onPointerDown, true);
            element.addEventListener("click", registration.onClick, true);
            window.addEventListener("pointermove", registration.onPointerMove, true);
            window.addEventListener("pointerup", registration.onPointerUp, true);
            window.addEventListener("pointercancel", registration.onPointerCancel, true);

            registrations.set(id, registration);
            return id;
        },

        refreshRegistration(element, dotNet, currentId) {
            if (!element) {
                return null;
            }

            if (currentId) {
                const current = registrations.get(currentId);
                if (current?.element === element) {
                    current.disposed = false;
                    current.dotNet = dotNet;
                    return currentId;
                }

                this.unregister(currentId);
            }

            return this.register(element, dotNet);
        },

        unregister(id) {
            const registration = registrations.get(id);
            if (!registration) {
                return;
            }

            registration.disposed = true;
            registration.panning = false;
            registration.pendingMove = null;

            if (registration.element) {
                registration.element.removeEventListener("pointerdown", registration.onPointerDown, true);
                registration.element.removeEventListener("click", registration.onClick, true);
            }

            window.removeEventListener("pointermove", registration.onPointerMove, true);
            window.removeEventListener("pointerup", registration.onPointerUp, true);
            window.removeEventListener("pointercancel", registration.onPointerCancel, true);
            registrations.delete(id);
        },

        getViewportMetrics(element) {
            if (!element) {
                return { width: 0, height: 0 };
            }

            const rect = element.getBoundingClientRect();
            return {
                width: Math.max(rect.width, element.clientWidth || 0),
                height: Math.max(rect.height, element.clientHeight || 0)
            };
        }
    };
})();
