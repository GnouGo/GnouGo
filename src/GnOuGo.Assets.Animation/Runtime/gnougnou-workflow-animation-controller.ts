export type GnouGnouWorkflowCharacterAction =
  | 'idle'
  | 'walk'
  | 'arrive'
  | 'pickup'
  | 'handoff'
  | 'type'
  | 'wait'
  | 'think'
  | 'deliver'
  | 'communicate'
  | 'clone'
  | 'merge'
  | 'celebrate'
  | 'fail'

export interface GnouGnouWorkflowCharacterController {
  startAmbient(): void
  stopAmbient(): void
  cancelAll(resetToIdle?: boolean): void
  stop(actorId: string, resetToIdle?: boolean): void
  play(
    actorId: string | undefined,
    action: GnouGnouWorkflowCharacterAction,
    duration: number,
    direction?: number,
  ): void
}

export interface WorkflowAnimationPrepared {
  svg: string
  width: number
  height: number
  seed: number
  scene: string
  entrypoint: string
  laneCount: number
  nodeCount: number
}

export interface WorkflowAnimationScenePatch {
  id: string
  svgFragment: string
  bounds: { width: number; height: number }
}

export interface WorkflowSimulationEvent {
  sequence: number
  type: string
  /**
   * Planned previews use a shared logical offset for events that must be
   * presented together (notably the actors travelling parallel branches).
   * Live telemetry may omit it.
   */
  offsetMs?: number
  durationMs: number
  workflowInstanceId?: string
  workflowName?: string
  actorId?: string
  targetActorId?: string
  stepId?: string
  stepType?: string
  stationId?: string
  nodeId?: string
  targetNodeId?: string
  edgeId?: string
  taskId?: string
  branchId?: string
  // Numeric values are accepted for compatibility with older Blazor JS
  // interop payloads. New Agent payloads use the canonical enum names.
  status?: 'Pending' | 'Running' | 'Succeeded' | 'Failed' | 'Skipped' | number
  progressCurrent?: number
  progressTotal?: number
  x?: number
  y?: number
  message?: string
}

export interface WorkflowAnimationControllerOptions {
  onFocus?: (id: string, event: WorkflowSimulationEvent) => void
  onStatus?: (status: string, message?: string) => void
  shouldFollowPortalTransfer?: () => boolean
  allowDocumentFocusScroll?: boolean
  cameraMode?: 'viewport' | 'scroll'
}

interface Position { x: number; y: number }
interface SceneBounds { width: number; height: number }
interface WorkflowSceneLayers {
  laneId: string
  workflowInstance?: string
  isDynamic: boolean
  background: SVGGElement
  actors: SVGGElement
}
interface TransitBranch {
  id: string
  parentActorId: string
  targetActorId: string
  parentLaneId: string
  targetLaneId: string
  workflowName: string
  group: SVGGElement
  sourcePortal: SVGGElement
  destinationPortal: SVGGElement
  routingAnchor: Position
  sourceAnchor: Position
  sourceStart: Position
  sourceEnd: Position
  destinationStart: Position
  destinationEnd: Position
  destinationAnchor: Position
  hasReturned: boolean
  activeTransferToken?: number
}
interface PortalTimeline {
  approachEnd: number
  sourceExitEnd: number
  destinationEntryStart: number
  destinationExitEnd: number
  total: number
}
interface ParallelCohort {
  id: string
  parentActorId: string
  workflowInstanceId?: string
  stepId?: string
  sequence: number
  parentCohortId?: string
  actorIds: Set<string>
  delegatedActorParents: Map<string, string>
}
type MotionMode = 'walk' | 'arc' | 'drop' | 'spawn' | 'merge' | 'sky'

const SVG_NAMESPACE = 'http://www.w3.org/2000/svg'
const LIVE_MOVEMENT_CAMERA_LEAD = .35

function easeInOut(value: number): number {
  return value < .5
    ? 4 * value * value * value
    : 1 - Math.pow(-2 * value + 2, 3) / 2
}

function actionForStep(stepType?: string): GnouGnouWorkflowCharacterAction {
  const normalized = stepType?.toLowerCase() ?? ''
  if (normalized.startsWith('human.')) return 'wait'
  if (normalized === 'llm' || normalized.startsWith('llm.')) return 'think'
  if (normalized === 'workflow.route') return 'communicate'
  if (normalized === 'workflow.plan') return 'type'
  if (normalized.startsWith('workflow.')) return 'handoff'
  if (normalized.startsWith('mcp.')) return 'communicate'
  return 'type'
}

function normalizedStatus(status?: WorkflowSimulationEvent['status']): string {
  if (typeof status === 'number')
    return ['pending', 'running', 'succeeded', 'failed', 'skipped'][status] ?? ''
  return typeof status === 'string' ? status.trim().toLowerCase() : ''
}

function isFailedStatus(status?: WorkflowSimulationEvent['status']): boolean {
  const normalized = normalizedStatus(status)
  return normalized === 'failed' || normalized === 'failure' || normalized === 'error'
}

function isSucceededStatus(status?: WorkflowSimulationEvent['status']): boolean {
  const normalized = normalizedStatus(status)
  return normalized === 'succeeded' || normalized === 'success' || normalized === 'completed'
}

/**
 * Shared workflow-scene controller used by the autonomous demo and Agent chat.
 * It owns scene motion; articulated character motion remains in Assets.Bears.
 */
export class GnouGnouWorkflowAnimationController {
  private readonly positions = new Map<string, Position>()
  private readonly frames = new Map<string, number>()
  private readonly stationAnimations: Animation[] = []
  private readonly liveEventQueue: WorkflowSimulationEvent[] = []
  private readonly persistentActionTimers = new Map<string, number>()
  private readonly sceneLayers = new Map<string, WorkflowSceneLayers>()
  private readonly transitBranches = new Map<string, TransitBranch>()
  private readonly parallelCohorts = new Map<string, ParallelCohort>()
  private readonly actorParallelCohorts = new Map<string, string>()
  private readonly terminalFailureActors = new Set<string>()
  private liveEventTimer: number | undefined
  private cameraFrame: number | undefined
  private cameraViewport: SceneBounds | undefined
  private sceneBounds: SceneBounds | undefined
  private activeLaneId: string | undefined
  private transitTransferSequence = 0
  private appliedEventCount = 0
  private generation = 0

  constructor(
    private readonly root: () => HTMLElement | null,
    private readonly characters: GnouGnouWorkflowCharacterController,
    private readonly options: WorkflowAnimationControllerOptions = {},
  ) {}

  attach() {
    this.generation += 1
    if (this.liveEventTimer !== undefined) window.clearTimeout(this.liveEventTimer)
    this.liveEventTimer = undefined
    this.liveEventQueue.length = 0
    this.persistentActionTimers.forEach(timer => window.clearTimeout(timer))
    this.persistentActionTimers.clear()
    this.frames.forEach(frame => cancelAnimationFrame(frame))
    this.frames.clear()
    this.root()?.querySelectorAll('.human-input-delivery').forEach(item => item.remove())
    this.stationAnimations.splice(0).forEach(animation => animation.cancel())
    this.stopCameraMotion()
    this.characters.cancelAll()
    this.positions.clear()
    this.sceneLayers.clear()
    this.transitBranches.clear()
    this.parallelCohorts.clear()
    this.actorParallelCohorts.clear()
    this.terminalFailureActors.clear()
    this.activeLaneId = undefined
    this.transitTransferSequence = 0
    this.cameraViewport = undefined
    this.sceneBounds = undefined
    this.appliedEventCount = 0
    this.setHostDiagnostic('data-animation-state', 'attached')
    this.setHostDiagnostic('data-animation-event-count', '0')
    this.setHostDiagnostic('data-animation-last-event', '')
    this.setHostDiagnostic('data-animation-error', '')
    this.setHostDiagnostic('data-animation-queued-events', '0')
    this.setHostDiagnostic('data-animation-parallel-actors', '0')
    this.setHostDiagnostic('data-animation-human-delivery', '')
    this.initializeCamera()
    this.initializeSceneLayers()
    this.characters.startAmbient()
  }

  dispose() {
    this.generation += 1
    if (this.liveEventTimer !== undefined) window.clearTimeout(this.liveEventTimer)
    this.liveEventTimer = undefined
    if (this.cameraFrame !== undefined) cancelAnimationFrame(this.cameraFrame)
    this.cameraFrame = undefined
    this.liveEventQueue.length = 0
    this.persistentActionTimers.forEach(timer => window.clearTimeout(timer))
    this.persistentActionTimers.clear()
    this.frames.forEach(frame => cancelAnimationFrame(frame))
    this.frames.clear()
    this.root()?.querySelectorAll('.human-input-delivery').forEach(item => item.remove())
    this.stationAnimations.splice(0).forEach(animation => animation.cancel())
    this.characters.cancelAll()
    this.characters.stopAmbient()
    this.positions.clear()
    this.sceneLayers.clear()
    this.transitBranches.clear()
    this.parallelCohorts.clear()
    this.actorParallelCohorts.clear()
    this.terminalFailureActors.clear()
    this.activeLaneId = undefined
    this.transitTransferSequence = 0
    this.cameraViewport = undefined
    this.sceneBounds = undefined
  }

  /**
   * Preserves visible motion when real telemetry arrives faster than the
   * browser can present it. This queue never delays the workflow itself.
   * Timer-driven consumers can continue to call applyEvent directly.
   */
  enqueueEvent(event: WorkflowSimulationEvent) {
    this.liveEventQueue.push(event)
    this.setHostDiagnostic('data-animation-queued-events', String(this.liveEventQueue.length))
    // Human Input is an authoritative synchronization point. The form can be
    // answered while normal presentation events are still queued, so drain
    // everything through the waiting event immediately. Otherwise an old
    // "previous step -> Human step" walk can replay when the answer arrives.
    if (event.type === 'human_input.waiting') {
      this.synchronizeHumanInputWaiting()
      return
    }
    // Give an NDJSON chunk one frame to contribute every event sharing the
    // same planned offset. Parallel branch movements can then start in one
    // presentation batch instead of being serialized by the live queue.
    if (this.liveEventTimer === undefined) {
      this.liveEventTimer = window.setTimeout(() => {
        this.liveEventTimer = undefined
        this.playNextLiveEvent()
      }, 16)
    }
  }

  applyScenePatch(patch: WorkflowAnimationScenePatch) {
    const svg = this.svgRoot()
    if (!svg || !patch.svgFragment) return
    this.initializeCamera()
    const parser = new DOMParser()
    const documentNode = parser.parseFromString(
      `<svg xmlns="${SVG_NAMESPACE}">${patch.svgFragment}</svg>`,
      'image/svg+xml',
    )
    const parsedRoot = documentNode.documentElement
    // Iterate over a stable snapshot. Importing clones a node and therefore
    // does not remove parsedRoot.firstChild; a while(firstChild) loop would
    // append forever and lock the browser on the first dynamic workflow.
    for (const child of Array.from(parsedRoot.childNodes))
      svg.append(document.importNode(child, true))
    this.initializeSceneLayers()
    this.promoteForeground(svg)
    this.sceneBounds = {
      width: Math.max(this.sceneBounds?.width ?? 0, patch.bounds.width),
      height: Math.max(this.sceneBounds?.height ?? 0, patch.bounds.height),
    }
    svg.dataset.sceneWidth = String(this.sceneBounds.width)
    svg.dataset.sceneHeight = String(this.sceneBounds.height)
    if (this.options.cameraMode === 'scroll') {
      svg.setAttribute('viewBox', `0 0 ${this.sceneBounds.width} ${this.sceneBounds.height}`)
      svg.setAttribute('width', String(this.sceneBounds.width))
      svg.setAttribute('height', String(this.sceneBounds.height))
    }
    this.options.onStatus?.('Running', 'A runtime workflow joined the scene.')
  }

  private promoteForeground(svg: SVGSVGElement) {
    const rootChildren = Array.from(svg.children)
    const fixedLayers = [
      'workflow-scene-backgrounds',
      'motion-trails',
      'gnougo-transit-system',
      'gnougo-transit-actors',
      'task-objects',
      'workflow-scene-actors',
      'gnougo-team',
    ]
      .map(id => rootChildren.find(element => element.id === id))
      .filter((element): element is Element => element !== undefined)

    // Moving the established layers (rather than cloning them) preserves the
    // state of every articulated GnOuGo while keeping the pipe and parcels
    // above roads and below the actors.
    for (const element of fixedLayers)
      svg.append(element)
  }

  private initializeSceneLayers() {
    const svg = this.svgRoot()
    if (!svg) return
    const backgroundRoot = this.ensureSvgGroup(svg, 'workflow-scene-backgrounds')
    const actorRoot = this.ensureSvgGroup(svg, 'workflow-scene-actors')
    this.ensureSvgGroup(svg, 'gnougo-transit-actors')

    backgroundRoot.querySelectorAll<SVGGElement>('[data-scene-lane-id]').forEach(background => {
      const laneId = background.dataset.sceneLaneId
      if (!laneId) return
      const actors = actorRoot.querySelector<SVGGElement>(
        `[data-scene-lane-id="${this.selectorValue(laneId)}"]`,
      )
      if (!actors) return
      const lane = this.find<SVGGElement>(laneId)
      const isDynamic = background.dataset.dynamicScene === 'true'
        || lane?.getAttribute('data-live-patch') === 'true'
      this.sceneLayers.set(laneId, {
        laneId,
        workflowInstance: lane?.getAttribute('data-workflow-instance') ?? undefined,
        isDynamic,
        background,
        actors,
      })
      if (background.classList.contains('is-scene-active')) this.activeLaneId = laneId
    })

    const laneIds = new Set(
      Array.from(svg.querySelectorAll<SVGGraphicsElement>('[data-lane-id]'))
        .map(element => element.getAttribute('data-lane-id'))
        .filter((laneId): laneId is string => Boolean(laneId)),
    )
    laneIds.forEach(laneId => {
      let scene = this.sceneLayers.get(laneId)
      if (!scene) {
        const lane = this.find<SVGGElement>(laneId)
        const liveActor = Array.from(
          svg.querySelectorAll<SVGGraphicsElement>('.gnougo-actor[data-live-actor="true"]'),
        ).some(actor => actor.getAttribute('data-lane-id') === laneId)
        const isDynamic = lane?.getAttribute('data-live-patch') === 'true' || liveActor
        const background = document.createElementNS(SVG_NAMESPACE, 'g')
        background.setAttribute('class', 'workflow-scene-layer workflow-scene-background')
        background.setAttribute('data-scene-lane-id', laneId)
        background.setAttribute('data-dynamic-scene', String(isDynamic))
        backgroundRoot.append(background)
        const actors = document.createElementNS(SVG_NAMESPACE, 'g')
        actors.setAttribute('class', 'workflow-scene-layer workflow-scene-actor-layer')
        actors.setAttribute('data-scene-lane-id', laneId)
        actors.setAttribute('data-dynamic-scene', String(isDynamic))
        actorRoot.append(actors)
        scene = {
          laneId,
          workflowInstance: lane?.getAttribute('data-workflow-instance') ?? undefined,
          isDynamic,
          background,
          actors,
        }
        this.sceneLayers.set(laneId, scene)
      }

      const candidates = Array.from(svg.querySelectorAll<SVGGraphicsElement>('[data-lane-id]'))
        .filter(element => element.getAttribute('data-lane-id') === laneId)
        .filter(element => !element.closest('.workflow-scene-layer'))
        .filter(element => !element.closest('#gnougo-transit-actors'))
        .filter(element => {
          const ancestor = element.parentElement?.closest<SVGGraphicsElement>('[data-lane-id]')
          return ancestor?.getAttribute('data-lane-id') !== laneId
        })
      candidates.forEach(element => {
        if (element.classList.contains('gnougo-actor')) scene!.actors.append(element)
        else scene!.background.append(element)
      })
    })

    if (!this.activeLaneId) {
      const masterLane = svg.querySelector<SVGGraphicsElement>(
        '.gnougo-actor[data-actor-kind="master"]',
      )?.getAttribute('data-lane-id')
      this.activeLaneId = masterLane || this.sceneLayers.keys().next().value
    }
    const activeScene = this.activeLaneId
      ? this.sceneLayers.get(this.activeLaneId)
      : undefined
    this.sceneLayers.forEach(scene => {
      if (!activeScene?.isDynamic && !scene.isDynamic) {
        this.setScenePosition(scene, 'active')
        return
      }
      if (scene === activeScene) {
        this.setScenePosition(scene, 'active')
        return
      }
      const alreadyPositioned = scene.background.classList.contains('is-scene-left')
        || scene.background.classList.contains('is-scene-right')
      if (!alreadyPositioned) this.setScenePosition(scene, 'right')
    })
    this.ensureTransitRoot(svg)
    this.syncParallelTaskVisibility()
    this.promoteForeground(svg)
  }

  private ensureSvgGroup(svg: SVGSVGElement, id: string): SVGGElement {
    const existing = this.find<SVGGElement>(id)
    if (existing) return existing
    const group = document.createElementNS(SVG_NAMESPACE, 'g')
    group.id = id
    svg.append(group)
    return group
  }

  private ensureTransitRoot(svg = this.svgRoot()): SVGGElement | null {
    if (!svg) return null
    const root = this.ensureSvgGroup(svg, 'gnougo-transit-system')
    root.setAttribute('aria-label', 'GnOuGo workflow transit portals')
    return root
  }

  private selectorValue(value: string): string {
    return value.replaceAll('\\', '\\\\').replaceAll('"', '\\"')
  }

  private setScenePosition(
    scene: WorkflowSceneLayers,
    position: 'active' | 'left' | 'right',
  ) {
    for (const layer of [scene.background, scene.actors]) {
      layer.classList.toggle('is-scene-active', position === 'active')
      layer.classList.toggle('is-scene-left', position === 'left')
      layer.classList.toggle('is-scene-right', position === 'right')
      layer.setAttribute('aria-hidden', position === 'active' ? 'false' : 'true')
    }
  }

  private activateSceneForActor(
    actorId: string | undefined,
    direction: 'forward' | 'reverse' = 'forward',
  ) {
    const actor = this.find<SVGGraphicsElement>(actorId)
    const laneId = actor?.getAttribute('data-lane-id')
    if (!laneId) {
      this.setLaneFocus(actorId)
      return
    }
    const target = this.sceneLayers.get(laneId)
    if (!target) return
    const previous = this.activeLaneId ? this.sceneLayers.get(this.activeLaneId) : undefined
    if (!target.isDynamic) {
      if (previous?.isDynamic)
        this.setScenePosition(previous, direction === 'reverse' ? 'right' : 'left')
      this.sceneLayers.forEach(scene => {
        if (!scene.isDynamic) this.setScenePosition(scene, 'active')
      })
      this.activeLaneId = laneId
      this.setLaneFocus(actorId)
      return
    }
    if (target === previous) {
      this.setLaneFocus(actorId)
      return
    }
    if (previous?.isDynamic)
      this.setScenePosition(previous, direction === 'reverse' ? 'right' : 'left')
    else {
      this.sceneLayers.forEach(scene => {
        if (!scene.isDynamic) this.setScenePosition(scene, 'left')
      })
    }
    this.setScenePosition(target, 'active')
    this.activeLaneId = laneId
    this.setLaneFocus(actorId)
  }

  private ensureTransitBranch(event: WorkflowSimulationEvent): TransitBranch | undefined {
    if (!event.actorId || !event.targetActorId) return undefined
    const existing = this.findTransitBranch(event.actorId, event.targetActorId)
    if (existing) return existing.branch
    const parent = this.find<SVGGraphicsElement>(event.actorId)
    const target = this.find<SVGGraphicsElement>(event.targetActorId)
    const parentLaneId = parent?.getAttribute('data-lane-id')
    const targetLaneId = target?.getAttribute('data-lane-id')
    const targetLane = this.find<SVGGraphicsElement>(targetLaneId ?? undefined)
    const isDynamicTarget = target?.getAttribute('data-live-actor') === 'true'
      || targetLane?.getAttribute('data-live-patch') === 'true'
    if (!isDynamicTarget) return undefined
    const root = this.ensureTransitRoot()
    if (!parentLaneId || !targetLaneId || !root) return undefined

    const safeId = `${event.actorId}-${event.targetActorId}`
      .replace(/[^a-zA-Z0-9_-]+/g, '-')
    const group = document.createElementNS(SVG_NAMESPACE, 'g')
    group.id = `transit-branch-${safeId}`
    group.setAttribute('class', 'gnougo-transit-branch is-ready')
    group.setAttribute('data-parent-actor-id', event.actorId)
    group.setAttribute('data-target-actor-id', event.targetActorId)
    group.setAttribute('data-has-returned', 'false')

    const sourcePortal = this.createTransitPortal(
      'transit-portal-source',
      event.workflowName ?? 'Routed workflow',
    )
    const destinationPortal = this.createTransitPortal(
      'transit-portal-destination',
      event.workflowName ?? 'Routed workflow',
    )
    group.append(sourcePortal, destinationPortal)
    root.append(group)

    const routingAnchor = (event.stationId && this.find(event.stationId))
      ? this.readPosition(event.stationId)
      : (event.nodeId && this.find(event.nodeId))
        ? this.readPosition(event.nodeId)
        : this.readPosition(event.actorId)
    const branch: TransitBranch = {
      id: group.id,
      parentActorId: event.actorId,
      targetActorId: event.targetActorId,
      parentLaneId,
      targetLaneId,
      workflowName: event.workflowName ?? 'Routed workflow',
      group,
      sourcePortal,
      destinationPortal,
      routingAnchor,
      sourceAnchor: routingAnchor,
      sourceStart: routingAnchor,
      sourceEnd: routingAnchor,
      destinationStart: routingAnchor,
      destinationEnd: routingAnchor,
      destinationAnchor: routingAnchor,
      hasReturned: false,
    }
    this.transitBranches.set(`${event.actorId}->${event.targetActorId}`, branch)
    this.layoutTransitBranch(branch, false)
    this.promoteForeground(this.svgRoot()!)
    return branch
  }

  private createTransitPortal(className: string, labelText: string): SVGGElement {
    const portal = document.createElementNS(SVG_NAMESPACE, 'g')
    portal.setAttribute('class', `transit-portal-leg ${className}`)
    const shell = document.createElementNS(SVG_NAMESPACE, 'path')
    shell.setAttribute('class', 'transit-pipe-shell')
    const core = document.createElementNS(SVG_NAMESPACE, 'path')
    core.setAttribute('class', 'transit-pipe-core')
    const highlight = document.createElementNS(SVG_NAMESPACE, 'path')
    highlight.setAttribute('class', 'transit-pipe-highlight')
    const leftMouth = document.createElementNS(SVG_NAMESPACE, 'g')
    leftMouth.setAttribute('class', 'transit-pipe-mouth transit-portal-left')
    leftMouth.innerHTML = '<circle r="38" class="transit-mouth-shell"/><circle r="30" class="transit-mouth-core"/>'
    const rightMouth = document.createElementNS(SVG_NAMESPACE, 'g')
    rightMouth.setAttribute('class', 'transit-pipe-mouth transit-portal-right')
    rightMouth.innerHTML = '<circle r="42" class="transit-mouth-shell"/><circle r="33" class="transit-mouth-core"/><path d="M8 -9L-6 0L8 9" class="transit-mouth-arrow"/>'
    const label = document.createElementNS(SVG_NAMESPACE, 'text')
    label.setAttribute('class', 'transit-pipe-label')
    label.textContent = labelText
    portal.append(shell, core, highlight, leftMouth, rightMouth, label)
    return portal
  }

  private workflowControlPosition(
    laneId: string,
    kinds: readonly string[],
  ): Position | undefined {
    const root = this.root()
    if (!root) return undefined
    const node = Array.from(
      root.querySelectorAll<SVGGraphicsElement>('.flow-node[data-node-kind]'),
    ).find(candidate =>
      candidate.getAttribute('data-lane-id') === laneId
      && kinds.includes(candidate.getAttribute('data-node-kind') ?? ''),
    )
    if (!node?.id) return undefined
    const position = this.readPosition(node.id)
    // Planned workflow control markers are nested below the node origin,
    // whereas runtime-added workflow markers are rendered directly at it.
    // Read the rendered structure so both portal mouths meet the visible
    // center instead of applying a blanket offset.
    const nestedControl = node.querySelector<SVGGraphicsElement>('.control-node')
    if (!nestedControl) return position
    const transform = nestedControl.getAttribute('transform') ?? ''
    const match = /translate\(\s*(-?[\d.]+)[ ,]+(-?[\d.]+)\s*\)/.exec(transform)
    return {
      x: position.x + Number(match?.[1] ?? 0),
      y: position.y + Number(match?.[2] ?? 0),
    }
  }

  private layoutTransitBranch(branch: TransitBranch, reverse: boolean) {
    const workflowAnchor = this.workflowControlPosition(
      branch.targetLaneId,
      reverse ? ['finish', 'return'] : ['start'],
    ) ?? this.readPosition(branch.targetActorId)
    const bounds = this.sceneBounds ?? { width: 1600, height: 900 }
    const margin = 52
    const portalLength = Math.min(240, Math.max(150, bounds.width * .15))
    const sourceAnchor = reverse ? workflowAnchor : branch.routingAnchor
    const destinationAnchor = reverse ? branch.routingAnchor : workflowAnchor
    const sourceRightX = Math.max(margin + 90, sourceAnchor.x - 72)
    const sourceLength = Math.min(portalLength, Math.max(90, sourceRightX - margin))
    const sourceStart = {
      x: sourceRightX,
      y: sourceAnchor.y,
    }
    const sourceEnd = {
      x: Math.max(margin, sourceStart.x - sourceLength),
      y: sourceStart.y,
    }
    const destinationLeftX = Math.min(
      bounds.width - margin - 90,
      destinationAnchor.x + 72,
    )
    const destinationLength = Math.min(
      portalLength,
      Math.max(90, bounds.width - margin - destinationLeftX),
    )
    const destinationEnd = {
      x: destinationLeftX,
      y: destinationAnchor.y,
    }
    const destinationStart = {
      x: Math.min(bounds.width - margin, destinationEnd.x + destinationLength),
      y: destinationEnd.y,
    }
    branch.sourceAnchor = sourceAnchor
    branch.sourceStart = sourceStart
    branch.sourceEnd = sourceEnd
    branch.destinationStart = destinationStart
    branch.destinationEnd = destinationEnd
    branch.destinationAnchor = destinationAnchor
    this.positionTransitPortal(branch.sourcePortal, sourceEnd, sourceStart)
    this.positionTransitPortal(branch.destinationPortal, destinationEnd, destinationStart)
  }

  private positionTransitPortal(
    portal: SVGGElement,
    left: Position,
    right: Position,
  ) {
    const path = `M ${left.x} ${left.y} L ${right.x} ${right.y}`
    portal.querySelectorAll<SVGPathElement>(
      '.transit-pipe-shell, .transit-pipe-core, .transit-pipe-highlight',
    ).forEach(element => element.setAttribute('d', path))
    portal.querySelector<SVGGElement>('.transit-portal-left')
      ?.setAttribute('transform', `translate(${left.x} ${left.y})`)
    portal.querySelector<SVGGElement>('.transit-portal-right')
      ?.setAttribute('transform', `translate(${right.x} ${right.y})`)
    const label = portal.querySelector<SVGTextElement>('.transit-pipe-label')
    label?.setAttribute('x', String((left.x + right.x) / 2))
    label?.setAttribute('y', String(left.y + 68))
  }

  private findTransitBranch(
    actorId?: string,
    targetActorId?: string,
  ): { branch: TransitBranch; reverse: boolean } | undefined {
    if (!actorId || !targetActorId) return undefined
    const forward = this.transitBranches.get(`${actorId}->${targetActorId}`)
    if (forward) return { branch: forward, reverse: false }
    const reverse = this.transitBranches.get(`${targetActorId}->${actorId}`)
    return reverse ? { branch: reverse, reverse: true } : undefined
  }

  applyEvent(event: WorkflowSimulationEvent) {
    const completingParallel = event.type === 'parallel.completed'
      ? this.parallelCohortForCompletion(event)
      : undefined
    this.updateParallelCohort(event)
    const actorPosition = event.actorId ? this.readPosition(event.actorId) : undefined
    const targetPosition = event.targetActorId ? this.readPosition(event.targetActorId) : undefined
    this.setFlowStatus(event)

    switch (event.type) {
      case 'simulation.started':
        this.options.onStatus?.('Running', event.message)
        break
      case 'workflow.discovered':
        this.ensureTransitBranch(event)
        this.setLaneFocus(event.actorId)
        this.characters.play(
          event.actorId,
          actionForStep(event.stepType),
          Math.max(700, event.durationMs),
        )
        this.pulseStation(event.stationId, Math.max(900, event.durationMs))
        this.options.onStatus?.('Running', event.message)
        break
      case 'workflow.started':
        this.activateSceneForActor(event.actorId)
        this.options.onStatus?.('Running', event.message)
        break
      case 'workflow.completed':
        if (isFailedStatus(event.status)) this.showTerminalFailure(event.actorId, 1600)
        this.options.onStatus?.(
          isFailedStatus(event.status) ? 'Failed' : 'Running',
          event.message,
        )
        break
      case 'actor.spawned': {
        this.show(event.actorId, true)
        if (event.actorId) {
          const destination = event.x !== undefined && event.y !== undefined
            ? { x: event.x, y: event.y }
            : this.readPosition(event.actorId)
          if (event.x !== undefined && event.y !== undefined)
            this.setPosition(event.actorId, { x: destination.x, y: destination.y - 120 }, 0, .35)
          this.animateMotion(event.actorId, destination, event.durationMs, 'spawn')
          this.characters.play(event.actorId, 'arrive', Math.max(500, event.durationMs))
        }
        break
      }
      case 'actor.moved':
        if (event.actorId) this.terminalFailureActors.delete(event.actorId)
        this.activateSceneForActor(event.actorId)
        this.stopPersistentAction(event.actorId)
        if (event.x !== undefined && event.y !== undefined) {
          const destination = { x: event.x, y: event.y }
          const direction = !actorPosition || destination.x >= actorPosition.x ? 1 : -1
          this.animateMotion(event.actorId, destination, event.durationMs, 'walk', event.edgeId)
          this.characters.play(event.actorId, 'walk', event.durationMs, direction)
          if (event.taskId)
            this.animateMotion(event.taskId, { x: destination.x + 64, y: destination.y - 82 }, event.durationMs, 'walk')
          this.pulseStation(event.stationId, event.durationMs + 300)
        }
        break
      case 'actor.waiting':
      case 'human_input.waiting':
        this.activateSceneForActor(event.actorId)
        this.settleHumanInputActor(event)
        this.playStepAction(event.actorId, 'wait', event.durationMs)
        this.pulseStation(event.stationId, Math.min(event.durationMs, 10_000))
        this.options.onStatus?.('Waiting for you', event.message)
        break
      case 'human_input.resumed':
        this.stopPersistentAction(event.actorId)
        if (event.actorId) this.characters.stop(event.actorId, false)
        this.animateHumanInputDelivery(event)
        this.options.onStatus?.('Running', event.message)
        break
      case 'actor.cloned':
        this.characters.play(event.actorId, 'clone', Math.max(600, event.durationMs))
        if (event.targetActorId) {
          this.show(event.targetActorId, true)
          const destination = event.x !== undefined && event.y !== undefined
            ? { x: event.x, y: event.y }
            : actorPosition ?? this.readPosition(event.targetActorId)
          if (actorPosition) this.setPosition(event.targetActorId, actorPosition, 0, .25)
          this.animateMotion(event.targetActorId, destination, event.durationMs, 'spawn')
          this.characters.play(event.targetActorId, 'clone', Math.max(600, event.durationMs))
        }
        break
      case 'task.cloned': {
        if (!event.taskId || !event.actorId) break
        const actor = this.find<SVGGraphicsElement>(event.actorId)
        const task = this.find<SVGGraphicsElement>(event.taskId)
        const position = this.readPosition(event.actorId)
        const laneId = actor?.getAttribute('data-lane-id')
        if (laneId) task?.setAttribute('data-lane-id', laneId)
        this.setPosition(event.taskId, { x: position.x + 20, y: position.y - 42 }, 0, .35)
        this.show(event.taskId, true)
        this.animateMotion(
          event.taskId,
          { x: position.x + 64, y: position.y - 82 },
          event.durationMs,
          'spawn',
        )
        this.syncParallelTaskVisibility()
        break
      }
      case 'task.merged':
        if (targetPosition)
          this.animateMotion(
            event.taskId,
            { x: targetPosition.x + 64, y: targetPosition.y - 82 },
            event.durationMs,
            'merge',
            undefined,
            true,
          )
        else
          this.show(event.taskId, false)
        break
      case 'actor.merged':
        this.characters.play(event.actorId, 'merge', Math.max(500, event.durationMs))
        this.characters.play(event.targetActorId, 'merge', Math.max(500, event.durationMs))
        if (targetPosition)
          this.animateMotion(event.actorId, targetPosition, event.durationMs, 'merge', undefined, true)
        break
      case 'task.dropped':
        if (event.x !== undefined && event.y !== undefined) {
          this.show(event.taskId, true)
          this.animateMotion(event.taskId, { x: event.x, y: event.y }, event.durationMs, 'drop')
        }
        break
      case 'task.picked_up':
        if (actorPosition) {
          this.animateMotion(event.taskId, { x: actorPosition.x + 68, y: actorPosition.y - 82 }, event.durationMs, 'arc')
          this.characters.play(event.actorId, 'pickup', Math.max(500, event.durationMs))
        }
        break
      case 'task.handed_off':
        this.stopPersistentAction(event.actorId)
        this.stopPersistentAction(event.targetActorId)
        if (targetPosition) {
          const direction = !actorPosition || targetPosition.x >= actorPosition.x ? 1 : -1
          const transit = this.findTransitBranch(event.actorId, event.targetActorId)
          if (transit) {
            // Dynamic transfers use two short, matched horizontal portals:
            // fade out beside Router/Return, swap scenes while invisible,
            // then reappear beside Start/Router in the same direction.
            this.layoutTransitBranch(transit.branch, transit.reverse)
            this.animateTransitActor(event, transit.branch)
            this.animateTransitParcel(event, transit.branch, transit.reverse)
          } else {
            this.animateMotion(event.taskId, { x: targetPosition.x + 68, y: targetPosition.y - 82 }, event.durationMs, 'arc')
            this.activateSceneForActor(event.targetActorId)
          }
          const actionDuration = transit
            ? Math.max(500, this.portalTransferDuration(event.durationMs))
            : Math.max(600, event.durationMs)
          const actionDirection = transit ? -1 : direction
          this.characters.play(event.actorId, transit ? 'walk' : 'handoff', actionDuration, actionDirection)
          this.characters.play(event.targetActorId, 'pickup', actionDuration, -actionDirection)
        }
        if (isFailedStatus(event.status)) this.setTaskStatus(event.taskId, 'Failed')
        break
      case 'step.started':
        if (event.actorId) this.terminalFailureActors.delete(event.actorId)
        this.activateSceneForActor(event.actorId)
        this.setActorStatus(event.actorId, 'Running')
        this.playStepAction(event.actorId, actionForStep(event.stepType), event.durationMs)
        this.pulseStation(event.stationId, Math.min(event.durationMs, 10_000))
        this.animateRoundabout(event.stationId, Math.min(event.durationMs, 60_000))
        break
      case 'step.completed': {
        const isHumanInputStep = event.stepType?.toLowerCase().startsWith('human.') === true
        // The waiting scene is already active. Re-activating it as the user
        // response arrives can replay the dynamic-scene entrance and make the
        // stationary GnOuGo disappear, then fall back in from above.
        if (!isHumanInputStep) this.activateSceneForActor(event.actorId)
        this.stopPersistentAction(event.actorId)
        this.setActorStatus(event.actorId, event.status)
        this.updateParcel(event.progressCurrent, event.progressTotal, isFailedStatus(event.status))
        // A successful Human Input completion must not touch the character rig:
        // the response capsule is the only moving element during receipt.
        if (isFailedStatus(event.status))
          this.showTerminalFailure(event.actorId, 1200)
        else if (!isHumanInputStep)
          this.characters.play(event.actorId, 'celebrate', 700)
        break
      }
      case 'output.sent':
        this.stopPersistentAction(event.actorId)
        if (event.x !== undefined && event.y !== undefined) {
          this.setTaskStatus(event.taskId, event.status)
          this.animateMotion(event.taskId, { x: event.x, y: event.y }, event.durationMs, 'sky', undefined, true)
          if (isFailedStatus(event.status))
            this.showTerminalFailure(event.actorId, Math.max(900, event.durationMs))
          else
            this.characters.play(event.actorId, 'deliver', Math.max(900, event.durationMs))
        }
        break
      case 'simulation.completed':
        this.stopPersistentAction(event.actorId)
        this.setActorStatus(event.actorId, event.status)
        if (isFailedStatus(event.status)) {
          this.setTaskStatus(event.taskId, 'Failed')
          this.showTerminalFailure(event.actorId, 1600)
        } else {
          this.characters.play(event.actorId, 'celebrate', 1600)
        }
        this.options.onStatus?.(isFailedStatus(event.status) ? 'Failed' : 'Completed', event.message)
        break
      case 'simulation.cancelled':
        this.stopPersistentAction(event.actorId)
        this.setTaskStatus(event.taskId, 'Failed')
        this.characters.play(event.actorId, 'fail', 1200)
        this.options.onStatus?.('Stopped', event.message)
        break
    }

    const focusId = this.focusIdForEvent(event)
    if (focusId) this.options.onFocus?.(focusId, event)
    if (completingParallel) this.removeParallelCohort(completingParallel)
  }

  private synchronizeHumanInputWaiting() {
    if (this.liveEventTimer !== undefined) window.clearTimeout(this.liveEventTimer)
    this.liveEventTimer = undefined
    const pending = this.liveEventQueue.splice(0)
    try {
      for (const event of pending) this.applyEvent(event)
      this.appliedEventCount += pending.length
      this.setHostDiagnostic('data-animation-state', 'waiting-for-human')
      this.setHostDiagnostic('data-animation-event-count', String(this.appliedEventCount))
      this.setHostDiagnostic('data-animation-last-event', 'human_input.waiting')
      this.setHostDiagnostic('data-animation-error', '')
    } catch (error) {
      const message = error instanceof Error ? error.message : String(error)
      this.setHostDiagnostic('data-animation-state', 'recovering')
      this.setHostDiagnostic('data-animation-error', message)
      console.error('[GnOuGo.Animation] Could not synchronize Human Input waiting state.', pending, error)
    } finally {
      this.setHostDiagnostic('data-animation-queued-events', '0')
    }
  }

  private settleHumanInputActor(event: WorkflowSimulationEvent) {
    if (!event.actorId || event.x === undefined || event.y === undefined) return
    for (const id of [event.actorId, event.taskId]) {
      if (!id) continue
      const frame = this.frames.get(id)
      if (frame !== undefined) cancelAnimationFrame(frame)
      this.frames.delete(id)
    }
    const destination = { x: event.x, y: event.y }
    this.setPosition(event.actorId, destination)
    if (event.taskId)
      this.setPosition(event.taskId, {
        x: destination.x + 64,
        y: destination.y - 82,
      })
  }

  private playNextLiveEvent() {
    const first = this.liveEventQueue.shift()
    if (!first) {
      this.liveEventTimer = undefined
      this.setHostDiagnostic('data-animation-queued-events', '0')
      return
    }

    const events = [first]
    // Offset zero is also used by unscheduled live telemetry, so only positive
    // planned offsets are safe concurrency keys.
    if (first.offsetMs !== undefined && first.offsetMs > 0) {
      while (this.liveEventQueue[0]?.offsetMs === first.offsetMs)
        events.push(this.liveEventQueue.shift()!)
    }
    this.setHostDiagnostic('data-animation-queued-events', String(this.liveEventQueue.length))
    try {
      for (const event of events) this.applyEvent(event)
      this.appliedEventCount += events.length
      this.setHostDiagnostic('data-animation-state', 'playing')
      this.setHostDiagnostic('data-animation-event-count', String(this.appliedEventCount))
      this.setHostDiagnostic('data-animation-last-event', events.at(-1)?.type ?? first.type)
      this.setHostDiagnostic('data-animation-error', '')
    } catch (error) {
      const message = error instanceof Error ? error.message : String(error)
      this.setHostDiagnostic('data-animation-state', 'recovering')
      this.setHostDiagnostic('data-animation-error', message)
      console.error('[GnOuGo.Animation] Could not apply live workflow event batch.', events, error)
    } finally {
      this.liveEventTimer = window.setTimeout(() => {
        this.liveEventTimer = undefined
        this.playNextLiveEvent()
      }, Math.max(...events.map(event => this.livePresentationGap(event))))
    }
  }

  private setHostDiagnostic(name: string, value: string) {
    this.root()?.setAttribute(name, value)
  }

  private livePresentationGap(event: WorkflowSimulationEvent): number {
    if (window.matchMedia('(prefers-reduced-motion: reduce)').matches) return 20
    switch (event.type) {
      case 'actor.moved':
        return Math.max(420, Math.min(event.durationMs, 900))
      case 'actor.spawned':
      case 'task.dropped':
      case 'task.picked_up':
      case 'task.cloned':
      case 'task.merged':
      case 'actor.cloned':
      case 'actor.merged':
        return Math.max(380, Math.min(event.durationMs, 900))
      case 'workflow.discovered':
        return Math.max(380, Math.min(event.durationMs, 700))
      case 'task.handed_off':
        return Math.max(1800, Math.min(this.portalTransferDuration(event.durationMs) + 180, 3400))
      case 'step.started':
        return 320
      case 'step.completed':
        return 360
      case 'output.sent':
        return 650
      case 'human_input.resumed':
        return Math.max(1100, Math.min(event.durationMs * 1.35, 1900))
      default:
        return 80
    }
  }

  private animateHumanInputDelivery(event: WorkflowSimulationEvent) {
    if (!event.actorId) return
    const svg = this.svgRoot()
    const host = this.root()
    const actor = this.find<SVGGraphicsElement>(event.actorId)
    if (!svg || !host || !actor) return

    const actorPosition = this.readPosition(event.actorId)
    const destination = {
      x: actorPosition.x + 72,
      y: actorPosition.y - 86,
    }
    const bounds = this.sceneBounds ?? {
      width: svg.viewBox.baseVal.width || 1600,
      height: svg.viewBox.baseVal.height || 900,
    }
    const svgRect = svg.getBoundingClientRect()
    const viewBox = svg.viewBox.baseVal
    const unitsPerPixelX = viewBox.width / Math.max(1, svgRect.width)
    const visibleLeft = viewBox.x + host.scrollLeft * unitsPerPixelX
    const visibleRight = viewBox.x
      + (host.scrollLeft + host.clientWidth) * unitsPerPixelX
    const rightStart = visibleRight + 90
    const leftStart = visibleLeft - 90
    const startX = rightStart <= bounds.width - 36
      ? rightStart
      : leftStart >= 36
        ? leftStart
        : bounds.width + 72
    const startY = Math.max(
      54,
      Math.min(bounds.height - 54, destination.y - 82),
    )
    const deliveryId = `human-input-delivery-${event.sequence}`
    this.find<SVGGraphicsElement>(deliveryId)?.remove()

    const delivery = document.createElementNS(SVG_NAMESPACE, 'g')
    delivery.id = deliveryId
    delivery.setAttribute('class', 'human-input-delivery')
    delivery.setAttribute('aria-hidden', 'true')
    delivery.setAttribute('transform', `translate(${startX} ${startY}) scale(.72)`)
    delivery.style.opacity = '0'
    delivery.innerHTML = [
      '<circle class="human-delivery-aura" r="42"/>',
      '<path class="human-delivery-balloon" d="M-30-25H30Q40-25 40-15V15Q40 25 30 25H8L-5 38-3 25H-30Q-40 25-40 15V-15Q-40-25-30-25Z"/>',
      '<circle class="human-delivery-dot" cx="-15" cy="0" r="4"/>',
      '<circle class="human-delivery-dot" cx="0" cy="0" r="4"/>',
      '<circle class="human-delivery-dot" cx="15" cy="0" r="4"/>',
      '<path class="human-delivery-check" d="M-14 9L-5 17 17-9"/>',
    ].join('')
    svg.append(delivery)
    this.setHostDiagnostic('data-animation-human-delivery', 'in-transit')

    const reducedMotion = window.matchMedia('(prefers-reduced-motion: reduce)').matches
    if (reducedMotion) {
      delivery.setAttribute('transform', `translate(${destination.x} ${destination.y})`)
      delivery.style.opacity = '1'
      const generation = this.generation
      window.setTimeout(() => {
        if (generation !== this.generation) {
          delivery.remove()
          return
        }
        delivery.remove()
        this.setHostDiagnostic('data-animation-human-delivery', 'received')
      }, 220)
      return
    }

    const duration = Math.max(1100, Math.min(event.durationMs * 1.35, 1900))
    const startedAt = performance.now()
    const generation = this.generation
    const animate = (now: number) => {
      if (generation !== this.generation || !delivery.isConnected) {
        this.frames.delete(deliveryId)
        delivery.remove()
        return
      }

      const progress = Math.max(0, Math.min(1, (now - startedAt) / duration))
      const eased = easeInOut(progress)
      const arcHeight = Math.min(92, 38 + Math.abs(destination.x - startX) * .1)
      const x = startX + (destination.x - startX) * eased
      const y = startY + (destination.y - startY) * eased
        - Math.sin(Math.PI * progress) * arcHeight
      const direction = destination.x >= startX ? 1 : -1
      const rotation = direction * (1 - eased) * 9
      const scale = .72 + Math.sin(Math.PI * progress) * .16 + eased * .28
      const opacity = progress < .12
        ? progress / .12
        : progress > .88
          ? (1 - progress) / .12
          : 1
      delivery.setAttribute(
        'transform',
        `translate(${x} ${y}) rotate(${rotation}) scale(${scale})`,
      )
      delivery.style.opacity = String(Math.max(0, Math.min(1, opacity)))

      if (progress < 1) {
        this.frames.set(deliveryId, requestAnimationFrame(animate))
        return
      }

      this.frames.delete(deliveryId)
      delivery.remove()
      this.setHostDiagnostic('data-animation-human-delivery', 'received')
    }
    this.frames.set(deliveryId, requestAnimationFrame(animate))
  }

  private playStepAction(
    actorId: string | undefined,
    action: GnouGnouWorkflowCharacterAction,
    durationMs: number,
  ) {
    if (!actorId) return
    this.stopPersistentAction(actorId)
    if (durationMs < 30_000) {
      this.characters.play(actorId, action, Math.max(1000, durationMs))
      return
    }

    const cycleMs = action === 'wait' ? 8_000 : action === 'type' ? 4_600 : 5_400
    const generation = this.generation
    const playCycle = () => {
      if (generation !== this.generation || !this.find(actorId)) {
        this.persistentActionTimers.delete(actorId)
        return
      }
      this.characters.play(actorId, action, cycleMs)
      const timer = window.setTimeout(playCycle, Math.ceil(cycleMs * 1.18))
      this.persistentActionTimers.set(actorId, timer)
    }
    playCycle()
  }

  private stopPersistentAction(actorId?: string) {
    if (!actorId) return
    const timer = this.persistentActionTimers.get(actorId)
    if (timer !== undefined) window.clearTimeout(timer)
    this.persistentActionTimers.delete(actorId)
  }

  private showTerminalFailure(actorId: string | undefined, durationMs: number) {
    if (!actorId || this.terminalFailureActors.has(actorId)) return
    this.terminalFailureActors.add(actorId)
    this.stopPersistentAction(actorId)
    this.setActorStatus(actorId, 'Failed')
    this.characters.play(actorId, 'fail', durationMs)
  }

  fitScene(behavior: ScrollBehavior = 'smooth') {
    if (this.options.cameraMode === 'scroll') return
    this.initializeCamera()
    const bounds = this.sceneBounds
    if (!bounds) return
    this.animateCamera(
      { x: 0, y: 0, width: bounds.width, height: bounds.height },
      behavior,
    )
  }

  panBy(deltaX: number, deltaY: number) {
    const host = this.root()
    this.stopCameraMotion()
    if (this.options.cameraMode === 'scroll') {
      host?.scrollBy({ left: deltaX, top: deltaY, behavior: 'auto' })
      return
    }

    this.initializeCamera()
    const svg = this.svgRoot()
    if (!svg) return
    const viewBox = svg.viewBox.baseVal
    const rect = svg.getBoundingClientRect()
    const unitsPerPixelX = viewBox.width / Math.max(1, rect.width)
    const unitsPerPixelY = viewBox.height / Math.max(1, rect.height)
    this.setCameraViewBox({
      x: viewBox.x + deltaX * unitsPerPixelX,
      y: viewBox.y + deltaY * unitsPerPixelY,
      width: viewBox.width,
      height: viewBox.height,
    })
  }

  stopCameraMotion() {
    if (this.cameraFrame !== undefined) cancelAnimationFrame(this.cameraFrame)
    this.cameraFrame = undefined
    const host = this.root()
    if (this.options.cameraMode === 'scroll' && host)
      host.scrollTo({ left: host.scrollLeft, top: host.scrollTop, behavior: 'auto' })
  }

  focusEvent(event: WorkflowSimulationEvent, behavior: ScrollBehavior = 'smooth') {
    const focusId = this.focusIdForEvent(event)
    if (!focusId) return undefined
    if (event.type === 'task.handed_off'
      && this.options.shouldFollowPortalTransfer?.() === false)
      return focusId
    const parallel = this.rootParallelCohort(this.parallelCohortForEvent(event))
    if (parallel && parallel.actorIds.size > 0) {
      this.followParallelCohort(
        parallel,
        behavior,
        Math.max(180, Math.min(event.durationMs || 680, 4000)),
      )
      return focusId
    }
    const destination = this.focusDestinationForEvent(event)
    const focusDuration = event.type === 'task.handed_off'
      ? this.portalLeadInDuration()
      : Math.min(event.durationMs, 680)
    this.focus(focusId, behavior, destination, focusDuration)
    return focusId
  }

  focus(
    id: string,
    behavior: ScrollBehavior = 'smooth',
    destination?: Position,
    durationMs?: number,
  ) {
    const host = this.root()
    const element = this.find<SVGGraphicsElement>(id)
    if (!host || !element) return
    this.stopCameraMotion()
    const resolvedBehavior = window.matchMedia('(prefers-reduced-motion: reduce)').matches ? 'auto' : behavior
    if (this.options.cameraMode !== 'scroll') {
      this.focusCamera(element, resolvedBehavior, destination, durationMs)
      return
    }
    const hasInternalViewport = host.scrollHeight > host.clientHeight + 2
      || host.scrollWidth > host.clientWidth + 2
    if (!hasInternalViewport) {
      if (this.options.allowDocumentFocusScroll === false) return
      element.scrollIntoView({
        behavior: resolvedBehavior,
        block: 'center',
        inline: 'center',
      })
      return
    }

    const hostRect = host.getBoundingClientRect()
    const elementRect = element.getBoundingClientRect()
    let elementCenterX = elementRect.left + elementRect.width / 2
    let elementCenterY = elementRect.top + elementRect.height / 2
    if (destination) {
      const svg = this.svgRoot()
      const svgRect = svg?.getBoundingClientRect()
      const viewBox = svg?.viewBox.baseVal
      if (svgRect && viewBox && viewBox.width > 0 && viewBox.height > 0) {
        elementCenterX = svgRect.left
          + (destination.x - viewBox.x) / viewBox.width * svgRect.width
        elementCenterY = svgRect.top
          + (destination.y - viewBox.y) / viewBox.height * svgRect.height
      }
    }
    host.scrollTo({
      left: Math.max(0, host.scrollLeft + elementCenterX - hostRect.left - host.clientWidth / 2),
      top: Math.max(0, host.scrollTop + elementCenterY - hostRect.top - host.clientHeight / 2),
      behavior: resolvedBehavior,
    })
  }

  private focusIdForEvent(event: WorkflowSimulationEvent): string | undefined {
    // The waiting event has already centered the human step. Re-centering on
    // resume (and again on completion) makes the stationary GnOuGo appear to
    // jump vertically even though only the response capsule is moving.
    if (event.type === 'human_input.resumed'
      || (event.type === 'step.completed'
        && event.stepType?.toLowerCase().startsWith('human.')))
      return undefined
    const collapsedStep = (event.type === 'step.started' || event.type === 'step.completed')
      && !event.targetNodeId
      && !event.stationId
      && !event.nodeId
    if (collapsedStep) return undefined
    if (event.type === 'actor.spawned' && event.actorId) {
      const actor = this.find<SVGGraphicsElement>(event.actorId)
      const laneId = actor?.getAttribute('data-lane-id')
      const scene = laneId ? this.sceneLayers.get(laneId) : undefined
      if (scene?.isDynamic && laneId !== this.activeLaneId) return undefined
    }
    if (event.type === 'task.handed_off') {
      const transit = this.findTransitBranch(event.actorId, event.targetActorId)
      return transit?.branch.id ?? event.targetActorId ?? event.actorId
    }
    const parallel = this.parallelCohortForEvent(event)
    if (parallel)
      return Array.from(parallel.actorIds).find(actorId => this.find(actorId))
        ?? parallel.parentActorId
    if (event.type === 'actor.cloned'
      || event.type === 'actor.merged')
      return event.targetActorId ?? event.actorId
    return event.targetNodeId ?? event.stationId ?? event.nodeId ?? event.actorId
  }

  private focusDestinationForEvent(event: WorkflowSimulationEvent): Position | undefined {
    if (event.type === 'task.handed_off') {
      const transit = this.findTransitBranch(event.actorId, event.targetActorId)
      if (transit) return this.portalSourceFocusPosition(transit.branch)
    }
    if (event.x === undefined || event.y === undefined) return undefined
    const destination = { x: event.x, y: event.y }
    if (event.type === 'actor.moved' && event.actorId) {
      // Moving the camera all the way to the station at the same time as the
      // actor makes the actor appear stationary inside a scrolling message
      // panel. Follow only leads part of the route during the walk; the
      // following step event finishes centering the destination after GnOuGo
      // has visibly travelled through the scene.
      return this.interpolatePosition(
        this.readPosition(event.actorId),
        destination,
        LIVE_MOVEMENT_CAMERA_LEAD,
      )
    }
    return event.type === 'actor.spawned' ? destination : undefined
  }

  private updateParallelCohort(event: WorkflowSimulationEvent) {
    if (event.type === 'parallel.started' && event.actorId) {
      const parentCohort = this.parallelCohortForActor(event.actorId)
      const id = [
        event.workflowInstanceId ?? 'workflow',
        event.stepId ?? 'parallel',
        event.sequence,
      ].join(':')
      this.parallelCohorts.set(id, {
        id,
        parentActorId: event.actorId,
        workflowInstanceId: event.workflowInstanceId,
        stepId: event.stepId,
        sequence: event.sequence,
        parentCohortId: parentCohort?.id,
        actorIds: new Set<string>(),
        delegatedActorParents: new Map<string, string>(),
      })
      this.setParallelDiagnostic()
      return
    }

    if (event.type === 'actor.cloned' && event.actorId && event.targetActorId) {
      const cohort = this.latestParallelCohortForParent(event.actorId, event.workflowInstanceId)
      if (!cohort) return
      cohort.actorIds.add(event.targetActorId)
      this.actorParallelCohorts.set(event.targetActorId, cohort.id)
      this.setParallelDiagnostic()
      return
    }

    if (event.type === 'task.handed_off' && event.actorId && event.targetActorId) {
      const sourceCohort = this.parallelCohortForActor(event.actorId)
      const targetCohort = this.parallelCohortForActor(event.targetActorId)
      if (sourceCohort && !targetCohort) {
        sourceCohort.actorIds.add(event.targetActorId)
        sourceCohort.delegatedActorParents.set(event.targetActorId, event.actorId)
        this.actorParallelCohorts.set(event.targetActorId, sourceCohort.id)
        this.setParallelDiagnostic()
        return
      }
      if (sourceCohort
        && targetCohort === sourceCohort
        && sourceCohort.delegatedActorParents.get(event.actorId) === event.targetActorId) {
        const generation = this.generation
        window.setTimeout(() => {
          if (generation !== this.generation || !this.parallelCohorts.has(sourceCohort.id)) return
          sourceCohort.actorIds.delete(event.actorId!)
          sourceCohort.delegatedActorParents.delete(event.actorId!)
          if (this.actorParallelCohorts.get(event.actorId!) === sourceCohort.id)
            this.actorParallelCohorts.delete(event.actorId!)
          this.setParallelDiagnostic()
        }, Math.max(16, event.durationMs))
      }
    }
  }

  private latestParallelCohortForParent(
    parentActorId: string,
    workflowInstanceId?: string,
  ): ParallelCohort | undefined {
    return Array.from(this.parallelCohorts.values())
      .filter(cohort => cohort.parentActorId === parentActorId
        && (!workflowInstanceId || cohort.workflowInstanceId === workflowInstanceId))
      .sort((left, right) => right.sequence - left.sequence)[0]
  }

  private parallelCohortForEvent(event: WorkflowSimulationEvent): ParallelCohort | undefined {
    if (event.type === 'parallel.completed')
      return this.parallelCohortForCompletion(event)
    const actorId = event.type === 'actor.cloned'
      ? event.targetActorId
      : event.actorId
    const actorCohort = this.parallelCohortForActor(actorId)
    if (actorCohort) return actorCohort
    if (event.type === 'parallel.started' && event.actorId)
      return this.latestParallelCohortForParent(event.actorId, event.workflowInstanceId)
    // A subordinate spawn precedes its handoff event. Keep Follow on the
    // already active cohort during that brief interval, then the handoff
    // explicitly enrolls the subordinate for the rest of its branch work.
    return Array.from(this.parallelCohorts.values())
      .filter(cohort => event.workflowInstanceId
        && cohort.workflowInstanceId === event.workflowInstanceId)
      .sort((left, right) => right.sequence - left.sequence)[0]
  }

  private parallelCohortForActor(actorId?: string): ParallelCohort | undefined {
    const cohortId = actorId ? this.actorParallelCohorts.get(actorId) : undefined
    return cohortId ? this.parallelCohorts.get(cohortId) : undefined
  }

  private rootParallelCohort(cohort?: ParallelCohort): ParallelCohort | undefined {
    let current = cohort
    const visited = new Set<string>()
    while (current?.parentCohortId && !visited.has(current.id)) {
      visited.add(current.id)
      current = this.parallelCohorts.get(current.parentCohortId) ?? current
      if (!current.parentCohortId) break
    }
    return current
  }

  private parallelCohortForCompletion(event: WorkflowSimulationEvent): ParallelCohort | undefined {
    if (!event.actorId) return undefined
    return Array.from(this.parallelCohorts.values())
      .filter(cohort => cohort.parentActorId === event.actorId
        && (!event.workflowInstanceId || cohort.workflowInstanceId === event.workflowInstanceId)
        && (!event.stepId || cohort.stepId === event.stepId))
      .sort((left, right) => right.sequence - left.sequence)[0]
  }

  private removeParallelCohort(cohort: ParallelCohort) {
    const removedIds = new Set([cohort.id])
    let foundDescendant = true
    while (foundDescendant) {
      foundDescendant = false
      for (const candidate of this.parallelCohorts.values()) {
        if (candidate.parentCohortId && removedIds.has(candidate.parentCohortId)
          && !removedIds.has(candidate.id)) {
          removedIds.add(candidate.id)
          foundDescendant = true
        }
      }
    }
    for (const id of removedIds) {
      const removed = this.parallelCohorts.get(id)
      if (!removed) continue
      for (const actorId of removed.actorIds) {
        if (this.actorParallelCohorts.get(actorId) === id)
          this.actorParallelCohorts.delete(actorId)
      }
      this.parallelCohorts.delete(id)
    }
    this.setParallelDiagnostic()
  }

  private parallelCentroid(cohort: ParallelCohort): Position | undefined {
    const actorIds = new Set<string>()
    for (const candidate of this.parallelCohorts.values()) {
      if (this.rootParallelCohort(candidate)?.id !== cohort.id) continue
      for (const actorId of candidate.actorIds) actorIds.add(actorId)
    }
    const positions = Array.from(actorIds)
      .filter(actorId => {
        const actor = this.find<SVGGraphicsElement>(actorId)
        if (!actor || actor.style.display === 'none' || Number(actor.style.opacity || 1) <= .01)
          return false
        const laneId = actor.getAttribute('data-lane-id')
        const scene = laneId ? this.sceneLayers.get(laneId) : undefined
        return !scene || scene.actors.classList.contains('is-scene-active')
      })
      .map(actorId => this.readPosition(actorId))
    if (positions.length === 0) return undefined
    return {
      x: positions.reduce((sum, position) => sum + position.x, 0) / positions.length,
      y: positions.reduce((sum, position) => sum + position.y, 0) / positions.length,
    }
  }

  private followParallelCohort(
    cohort: ParallelCohort,
    behavior: ScrollBehavior,
    durationMs: number,
  ) {
    const reduced = window.matchMedia('(prefers-reduced-motion: reduce)').matches
    if (reduced || behavior === 'auto') {
      const center = this.parallelCentroid(cohort)
      if (center) this.centerCameraAt(center)
      return
    }

    this.stopCameraMotion()
    const startedAt = performance.now()
    const generation = this.generation
    const render = (now: number) => {
      if (generation !== this.generation || !this.parallelCohorts.has(cohort.id)) {
        this.cameraFrame = undefined
        return
      }
      const center = this.parallelCentroid(cohort)
      if (center) this.centerCameraAt(center)
      if (now - startedAt < durationMs) {
        this.cameraFrame = requestAnimationFrame(render)
        return
      }
      this.cameraFrame = undefined
    }
    this.cameraFrame = requestAnimationFrame(render)
  }

  private centerCameraAt(center: Position) {
    const host = this.root()
    const svg = this.svgRoot()
    if (!host || !svg) return
    if (this.options.cameraMode !== 'scroll') {
      this.initializeCamera()
      const viewport = this.cameraViewport
      if (!viewport) return
      this.setCameraViewBox({
        x: center.x - viewport.width / 2,
        y: center.y - viewport.height / 2,
        width: viewport.width,
        height: viewport.height,
      })
      return
    }

    const svgRect = svg.getBoundingClientRect()
    const hostRect = host.getBoundingClientRect()
    const viewBox = svg.viewBox.baseVal
    if (viewBox.width <= 0 || viewBox.height <= 0) return
    const centerX = svgRect.left + (center.x - viewBox.x) / viewBox.width * svgRect.width
    const centerY = svgRect.top + (center.y - viewBox.y) / viewBox.height * svgRect.height
    host.scrollTo({
      left: Math.max(0, host.scrollLeft + centerX - hostRect.left - host.clientWidth / 2),
      top: Math.max(0, host.scrollTop + centerY - hostRect.top - host.clientHeight / 2),
      behavior: 'auto',
    })
  }

  private setParallelDiagnostic() {
    const actorCount = Array.from(this.parallelCohorts.values())
      .reduce((count, cohort) => count + cohort.actorIds.size, 0)
    this.setHostDiagnostic('data-animation-parallel-actors', String(actorCount))
  }

  private portalSourceFocusPosition(branch: TransitBranch): Position {
    return {
      x: (branch.sourceStart.x + branch.sourceEnd.x) / 2,
      y: (branch.sourceStart.y + branch.sourceEnd.y) / 2,
    }
  }

  private portalDestinationFocusPosition(branch: TransitBranch): Position {
    return {
      x: (branch.destinationStart.x + branch.destinationEnd.x) / 2,
      y: (branch.destinationStart.y + branch.destinationEnd.y) / 2,
    }
  }

  private initializeCamera() {
    if (this.cameraViewport && this.sceneBounds) return
    const svg = this.svgRoot()
    if (!svg) return
    const viewBox = svg.viewBox.baseVal
    const width = viewBox.width || Number(svg.getAttribute('width')) || 1
    const height = viewBox.height || Number(svg.getAttribute('height')) || 1
    this.cameraViewport ??= { width, height }
    this.sceneBounds ??= {
      width: Number(svg.dataset.sceneWidth) || Math.max(width, viewBox.x + width),
      height: Number(svg.dataset.sceneHeight) || Math.max(height, viewBox.y + height),
    }
    svg.dataset.cameraMode = this.options.cameraMode ?? 'viewport'
    svg.dataset.sceneWidth = String(this.sceneBounds.width)
    svg.dataset.sceneHeight = String(this.sceneBounds.height)
  }

  private focusCamera(
    element: SVGGraphicsElement,
    behavior: ScrollBehavior,
    destination?: Position,
    durationMs?: number,
  ) {
    this.initializeCamera()
    const viewport = this.cameraViewport
    if (!viewport) return
    const center = destination ?? this.elementWorldCenter(element)
    this.animateCamera({
      x: center.x - viewport.width / 2,
      y: center.y - viewport.height / 2,
      width: viewport.width,
      height: viewport.height,
    }, behavior, durationMs)
  }

  private elementWorldCenter(element: SVGGraphicsElement): Position {
    const transform = element.getAttribute('transform') ?? ''
    const translated = /translate\(\s*(-?[\d.]+)[ ,]+(-?[\d.]+)\s*\)/.exec(transform)
    if (translated)
      return { x: Number(translated[1]), y: Number(translated[2]) }
    try {
      const bounds = element.getBBox()
      return {
        x: bounds.x + bounds.width / 2,
        y: bounds.y + bounds.height / 2,
      }
    } catch {
      return { x: 0, y: 0 }
    }
  }

  private animateCamera(
    target: { x: number; y: number; width: number; height: number },
    behavior: ScrollBehavior,
    requestedDurationMs?: number,
  ) {
    const svg = this.svgRoot()
    if (!svg) return
    this.stopCameraMotion()
    const current = svg.viewBox.baseVal
    const from = {
      x: current.x,
      y: current.y,
      width: current.width,
      height: current.height,
    }
    const clamped = this.clampCamera(target)
    const alreadyFocused = Math.abs(from.x - clamped.x) < .5
      && Math.abs(from.y - clamped.y) < .5
      && Math.abs(from.width - clamped.width) < .5
      && Math.abs(from.height - clamped.height) < .5
    if (alreadyFocused) {
      this.setCameraViewBox(clamped)
      this.cameraFrame = undefined
      return
    }
    if (behavior === 'auto') {
      this.setCameraViewBox(clamped)
      this.cameraFrame = undefined
      return
    }

    const startedAt = performance.now()
    const duration = requestedDurationMs === undefined
      ? 680
      : Math.max(180, Math.min(1100, requestedDurationMs))
    const generation = this.generation
    const render = (now: number) => {
      if (generation !== this.generation || !svg.isConnected) return
      const progress = Math.max(0, Math.min(1, (now - startedAt) / duration))
      const eased = easeInOut(progress)
      this.setCameraViewBox({
        x: from.x + (clamped.x - from.x) * eased,
        y: from.y + (clamped.y - from.y) * eased,
        width: from.width + (clamped.width - from.width) * eased,
        height: from.height + (clamped.height - from.height) * eased,
      })
      if (progress < 1) {
        this.cameraFrame = requestAnimationFrame(render)
        return
      }
      this.cameraFrame = undefined
    }
    this.cameraFrame = requestAnimationFrame(render)
  }

  private setCameraViewBox(viewBox: { x: number; y: number; width: number; height: number }) {
    const svg = this.svgRoot()
    if (!svg) return
    const clamped = this.clampCamera(viewBox)
    svg.setAttribute(
      'viewBox',
      `${clamped.x} ${clamped.y} ${clamped.width} ${clamped.height}`,
    )
  }

  private clampCamera(viewBox: { x: number; y: number; width: number; height: number }) {
    const bounds = this.sceneBounds ?? viewBox
    const width = Math.max(1, Math.min(viewBox.width, Math.max(1, bounds.width)))
    const height = Math.max(1, Math.min(viewBox.height, Math.max(1, bounds.height)))
    return {
      x: Math.max(0, Math.min(viewBox.x, Math.max(0, bounds.width - width))),
      y: Math.max(0, Math.min(viewBox.y, Math.max(0, bounds.height - height))),
      width,
      height,
    }
  }

  private setLaneFocus(actorId?: string) {
    const host = this.root()
    const actor = this.find<SVGGraphicsElement>(actorId)
    const laneId = actor?.getAttribute('data-lane-id')
    if (!host || !laneId) return
    const lane = this.find<SVGGraphicsElement>(laneId)
    const workflowInstance = lane?.getAttribute('data-workflow-instance')
    host.querySelectorAll<SVGGraphicsElement>(
      '[data-lane-id], [data-workflow-instance-id]',
    ).forEach(element => {
      if (element.closest('#gnougo-transit-actors')) {
        element.classList.remove('is-scene-muted')
        return
      }
      const belongsToLane = element.getAttribute('data-lane-id') === laneId
      const belongsToWorkflow = workflowInstance !== undefined && workflowInstance !== null
        && element.getAttribute('data-workflow-instance-id') === workflowInstance
      element.classList.toggle('is-scene-muted', !belongsToLane && !belongsToWorkflow)
    })
    this.syncParallelTaskVisibility()
  }

  private syncParallelTaskVisibility() {
    this.root()?.querySelectorAll<SVGGraphicsElement>(
      '.task-object[data-task-kind="branch"]',
    ).forEach(task => {
      const laneId = task.getAttribute('data-lane-id')
      const scene = laneId ? this.sceneLayers.get(laneId) : undefined
      const sceneVisible = !scene
        || scene.background.classList.contains('is-scene-active')
      task.classList.toggle('is-parallel-detail-hidden', !sceneVisible)
    })
  }

  private svgRoot(): SVGSVGElement | null {
    return this.root()?.querySelector<SVGSVGElement>('svg') ?? null
  }

  private find<T extends Element>(id?: string): T | null {
    if (!id) return null
    const root = this.root()
    if (!root) return null
    const escape = globalThis.CSS?.escape
    if (escape) {
      try {
        return root.querySelector<T>(`#${escape(id)}`)
      } catch {
        // Fall through to an exact id comparison for older embedded webviews.
      }
    }
    return Array.from(root.querySelectorAll<T>('[id]'))
      .find(element => element.id === id) ?? null
  }

  private readPosition(id: string): Position {
    const known = this.positions.get(id)
    if (known) return known
    const transform = this.find<SVGGraphicsElement>(id)?.getAttribute('transform') ?? ''
    const match = /translate\((-?[\d.]+)[ ,](-?[\d.]+)\)/.exec(transform)
    const position = match ? { x: Number(match[1]), y: Number(match[2]) } : { x: 0, y: 0 }
    this.positions.set(id, position)
    return position
  }

  private show(id?: string, visible = true) {
    const element = this.find<SVGGraphicsElement>(id)
    if (!element) return
    element.setAttribute('data-visible', visible ? 'true' : 'false')
    element.style.opacity = visible ? '1' : '0'
  }

  private setPosition(id: string, position: Position, rotation = 0, scale = 1) {
    const element = this.find<SVGGraphicsElement>(id)
    if (!element) return
    element.style.transform = ''
    element.setAttribute('transform', `translate(${position.x} ${position.y}) rotate(${rotation}) scale(${scale})`)
    this.positions.set(id, position)
  }

  private transitDuration(durationMs: number): number {
    if (window.matchMedia('(prefers-reduced-motion: reduce)').matches) return 1
    return Math.max(700, Math.min(durationMs * 1.45, 2200))
  }

  private portalLeadInDuration(): number {
    return window.matchMedia('(prefers-reduced-motion: reduce)').matches ? 0 : 420
  }

  private portalSceneSwapDuration(): number {
    return window.matchMedia('(prefers-reduced-motion: reduce)').matches ? 0 : 600
  }

  private portalTimeline(durationMs: number): PortalTimeline {
    const motionDuration = this.transitDuration(durationMs)
    const approachEnd = motionDuration * .15
    const sourceExitEnd = approachEnd + motionDuration * .35
    const destinationEntryStart = sourceExitEnd + this.portalSceneSwapDuration()
    const destinationExitEnd = destinationEntryStart + motionDuration * .35
    return {
      approachEnd,
      sourceExitEnd,
      destinationEntryStart,
      destinationExitEnd,
      total: destinationExitEnd + motionDuration * .15,
    }
  }

  private portalTransferDuration(durationMs: number): number {
    return this.portalLeadInDuration() + this.portalTimeline(durationMs).total
  }

  private animateTransitActor(
    event: WorkflowSimulationEvent,
    branch: TransitBranch,
  ) {
    if (!event.actorId) return
    const actor = this.find<SVGGraphicsElement>(event.actorId)
    const transitRoot = this.find<SVGGElement>('gnougo-transit-actors')
    if (!actor || !transitRoot || !actor.parentNode) return

    const previous = this.frames.get(event.actorId)
    if (previous !== undefined) cancelAnimationFrame(previous)
    const originalParent = actor.parentNode
    const originalNextSibling = actor.nextSibling
    const originalTransform = actor.getAttribute('transform')
    const originalStyleOpacity = actor.style.opacity
    const originalPosition = this.readPosition(event.actorId)
    const targetActor = this.find<SVGGraphicsElement>(event.targetActorId)
    const targetStyleOpacity = targetActor?.style.opacity
    const timeline = this.portalTimeline(event.durationMs)
    const startedAt = performance.now() + this.portalLeadInDuration()
    const generation = this.generation
    actor.classList.add('is-in-transit')
    transitRoot.append(actor)
    actor.style.opacity = '1'
    if (targetActor) targetActor.style.opacity = '0'

    const restore = () => {
      if (originalParent.isConnected) {
        if (originalNextSibling?.parentNode === originalParent)
          originalParent.insertBefore(actor, originalNextSibling)
        else
          originalParent.appendChild(actor)
      }
      if (originalTransform === null) actor.removeAttribute('transform')
      else actor.setAttribute('transform', originalTransform)
      actor.style.opacity = originalStyleOpacity
      if (targetActor && event.targetActorId) {
        this.setPosition(event.targetActorId, branch.destinationAnchor)
        targetActor.style.opacity = targetStyleOpacity ?? '1'
      }
      actor.classList.remove('is-in-transit')
      this.positions.set(event.actorId!, originalPosition)
      this.frames.delete(event.actorId!)
    }

    const render = (now: number) => {
      if (generation !== this.generation || !actor.isConnected) return
      const elapsed = Math.max(0, Math.min(timeline.total, now - startedAt))
      let position: Position
      let scale: number
      let opacity: number
      let rotation = 0
      if (elapsed < timeline.approachEnd) {
        const local = easeInOut(elapsed / timeline.approachEnd)
        position = this.interpolatePosition(originalPosition, branch.sourceStart, local)
        position.y -= Math.sin(local * Math.PI) * 18
        scale = 1
        opacity = 1
      } else if (elapsed < timeline.sourceExitEnd) {
        const local = easeInOut(
          (elapsed - timeline.approachEnd)
          / (timeline.sourceExitEnd - timeline.approachEnd),
        )
        position = this.interpolatePosition(branch.sourceStart, branch.sourceEnd, local)
        scale = 1 - local * .42
        opacity = 1 - local
        rotation = -Math.sin(local * Math.PI) * 5
      } else if (elapsed < timeline.destinationEntryStart) {
        position = branch.destinationStart
        scale = .58
        opacity = 0
      } else if (elapsed < timeline.destinationExitEnd) {
        const local = easeInOut(
          (elapsed - timeline.destinationEntryStart)
          / (timeline.destinationExitEnd - timeline.destinationEntryStart),
        )
        position = this.interpolatePosition(branch.destinationStart, branch.destinationEnd, local)
        scale = .58 + local * .42
        opacity = local
        rotation = -Math.sin(local * Math.PI) * 5
      } else {
        const local = easeInOut(
          (elapsed - timeline.destinationExitEnd)
          / (timeline.total - timeline.destinationExitEnd),
        )
        position = this.interpolatePosition(branch.destinationEnd, branch.destinationAnchor, local)
        position.y -= Math.sin(local * Math.PI) * 18
        scale = 1
        opacity = 1
      }
      actor.setAttribute(
        'transform',
        `translate(${position.x} ${position.y}) rotate(${rotation}) scale(${scale})`,
      )
      actor.style.opacity = String(opacity)
      this.positions.set(event.actorId!, position)
      if (elapsed < timeline.total) {
        this.frames.set(event.actorId!, requestAnimationFrame(render))
        return
      }
      restore()
    }
    this.frames.set(event.actorId, requestAnimationFrame(render))
  }

  private animateTransitParcel(
    event: WorkflowSimulationEvent,
    branch: TransitBranch,
    reverse: boolean,
  ) {
    if (!event.taskId) {
      this.activateSceneForActor(event.targetActorId, reverse ? 'reverse' : 'forward')
      return
    }

    const siblings = Array.from(this.transitBranches.values())
      .filter(candidate => candidate.parentActorId === branch.parentActorId)
    const sourceParcel = this.find<SVGGraphicsElement>(event.taskId)
    let visualId = event.taskId
    let ephemeral = false
    if (!reverse && siblings.length > 1 && sourceParcel) {
      const clone = sourceParcel.cloneNode(true) as SVGGraphicsElement
      visualId = `${event.taskId}-transit-${branch.targetActorId}`
        .replace(/[^a-zA-Z0-9_-]+/g, '-')
      clone.id = visualId
      clone.setAttribute('data-transit-parcel', branch.id)
      clone.classList.add('is-transit-copy')
      clone.querySelectorAll('[id]').forEach(element => element.removeAttribute('id'))
      ;(this.find<SVGGElement>('task-objects') ?? this.svgRoot())?.append(clone)
      const sourcePosition = this.readPosition(event.taskId)
      clone.setAttribute('transform', `translate(${sourcePosition.x} ${sourcePosition.y})`)
      this.positions.set(visualId, sourcePosition)
      ephemeral = true
      this.show(event.taskId, false)
    }

    const parcel = this.find<SVGGraphicsElement>(visualId)
    if (!parcel) return
    const previous = this.frames.get(visualId)
    if (previous !== undefined) cancelAnimationFrame(previous)
    const from = this.readPosition(visualId)
    const destination = {
      x: branch.destinationAnchor.x + 68,
      y: branch.destinationAnchor.y - 82,
    }
    const sourceStart = { x: branch.sourceStart.x, y: branch.sourceStart.y - 68 }
    const sourceEnd = { x: branch.sourceEnd.x, y: branch.sourceEnd.y - 68 }
    const destinationStart = { x: branch.destinationStart.x, y: branch.destinationStart.y - 68 }
    const destinationEnd = { x: branch.destinationEnd.x, y: branch.destinationEnd.y - 68 }
    const timeline = this.portalTimeline(event.durationMs)
    const startedAt = performance.now() + this.portalLeadInDuration()
    const generation = this.generation
    let sceneChanged = false
    let destinationPortalVisible = false
    const transferToken = ++this.transitTransferSequence
    branch.activeTransferToken = transferToken
    branch.group.classList.remove('is-parked')
    branch.group.classList.add('is-active')
    branch.group.classList.toggle('is-returning', reverse)
    branch.group.setAttribute('data-transit-direction', reverse ? 'return' : 'outbound')
    branch.group.setAttribute('data-portal-phase', 'preparing')
    parcel.classList.add('is-in-transit')
    this.show(visualId, true)
    let sourcePortalVisible = false

    const render = (now: number) => {
      if (generation !== this.generation || !parcel.isConnected) return
      const sourceRevealAt = startedAt - 180
      if (!sourcePortalVisible && now >= sourceRevealAt) {
        sourcePortalVisible = true
        branch.group.setAttribute('data-portal-phase', 'source')
      }
      const elapsed = Math.max(0, Math.min(timeline.total, now - startedAt))
      let position: Position
      let opacity: number
      let scale: number
      let rotationProgress: number
      if (elapsed < timeline.approachEnd) {
        const local = easeInOut(elapsed / timeline.approachEnd)
        position = this.interpolatePosition(from, sourceStart, local)
        position.y -= Math.sin(local * Math.PI) * 22
        opacity = 1
        scale = 1
        rotationProgress = local * .15
      } else if (elapsed < timeline.sourceExitEnd) {
        const local = easeInOut(
          (elapsed - timeline.approachEnd)
          / (timeline.sourceExitEnd - timeline.approachEnd),
        )
        position = this.interpolatePosition(sourceStart, sourceEnd, local)
        opacity = 1 - local
        scale = 1 - local * .48
        rotationProgress = .15 + local * .35
      } else if (elapsed < timeline.destinationEntryStart) {
        position = destinationStart
        opacity = 0
        scale = .52
        rotationProgress = .5
      } else if (elapsed < timeline.destinationExitEnd) {
        const local = easeInOut(
          (elapsed - timeline.destinationEntryStart)
          / (timeline.destinationExitEnd - timeline.destinationEntryStart),
        )
        position = this.interpolatePosition(destinationStart, destinationEnd, local)
        opacity = local
        scale = .52 + local * .48
        rotationProgress = .5 + local * .35
      } else {
        const local = easeInOut(
          (elapsed - timeline.destinationExitEnd)
          / (timeline.total - timeline.destinationExitEnd),
        )
        position = this.interpolatePosition(destinationEnd, destination, local)
        position.y -= Math.sin(local * Math.PI) * 22
        opacity = 1
        scale = 1
        rotationProgress = .85 + local * .15
      }
      const rotation = -rotationProgress * 360
      parcel.setAttribute(
        'transform',
        `translate(${position.x} ${position.y}) rotate(${rotation}) scale(${scale})`,
      )
      parcel.style.opacity = String(opacity)
      this.positions.set(visualId, position)

      if (!sceneChanged && elapsed >= timeline.sourceExitEnd) {
        sceneChanged = true
        branch.group.setAttribute('data-portal-phase', 'between')
        this.activateSceneForActor(event.targetActorId, reverse ? 'reverse' : 'forward')
        const shouldFollow = this.options.shouldFollowPortalTransfer?.()
          ?? this.options.onFocus !== undefined
        if (shouldFollow) {
          this.focus(
            branch.id,
            'smooth',
            this.portalDestinationFocusPosition(branch),
            this.portalSceneSwapDuration() * .7,
          )
        }
      }
      const destinationRevealAt = timeline.destinationEntryStart - 180
      if (!destinationPortalVisible && elapsed >= destinationRevealAt) {
        destinationPortalVisible = true
        branch.group.setAttribute('data-portal-phase', 'destination')
      }
      if (elapsed < timeline.total) {
        this.frames.set(visualId, requestAnimationFrame(render))
        return
      }

      this.frames.delete(visualId)
      if (branch.activeTransferToken === transferToken) {
        branch.activeTransferToken = undefined
        branch.group.classList.remove('is-active', 'is-returning')
        branch.group.removeAttribute('data-transit-direction')
        if (reverse) {
          // Once work has returned, leave the main-workflow mouth visible as
          // a quiet landmark beside the routing roundabout.
          branch.hasReturned = true
          branch.group.setAttribute('data-has-returned', 'true')
          branch.group.classList.add('is-parked')
          branch.group.setAttribute('data-portal-phase', 'parked-parent')
        } else {
          branch.group.classList.remove('is-parked')
          branch.group.removeAttribute('data-portal-phase')
        }
      }
      parcel.setAttribute('transform', `translate(${destination.x} ${destination.y})`)
      parcel.style.opacity = '1'
      parcel.classList.remove('is-in-transit')
      this.positions.set(visualId, destination)
      if (ephemeral) {
        parcel.remove()
        this.positions.delete(visualId)
        this.setPosition(event.taskId!, destination)
        this.show(event.taskId, true)
      }
    }
    this.frames.set(visualId, requestAnimationFrame(render))
  }

  private interpolatePosition(from: Position, to: Position, progress: number): Position {
    return {
      x: from.x + (to.x - from.x) * progress,
      y: from.y + (to.y - from.y) * progress,
    }
  }

  private animateMotion(
    id: string | undefined,
    target: Position,
    duration: number,
    mode: MotionMode,
    pathId?: string,
    hideAfter = false,
  ) {
    if (!id) return
    const element = this.find<SVGGraphicsElement>(id)
    if (!element) return
    const previous = this.frames.get(id)
    if (previous !== undefined) cancelAnimationFrame(previous)
    const from = this.readPosition(id)
    const reduced = window.matchMedia('(prefers-reduced-motion: reduce)').matches
    const actualDuration = reduced ? 1 : Math.max(16, Math.min(duration, 4000))
    const startedAt = performance.now()
    const generation = this.generation
    const route = pathId
      ? this.find<SVGGraphicsElement>(pathId)?.querySelector<SVGPathElement>('[data-route-path="true"]') ?? null
      : null
    const routeLength = route?.getTotalLength() ?? 0
    const routeStart = route && routeLength > 0 ? route.getPointAtLength(0) : undefined
    const routeEnd = route && routeLength > 0 ? route.getPointAtLength(routeLength) : undefined
    this.show(id, true)

    const render = (now: number) => {
      if (generation !== this.generation || !element.isConnected) return
      const progress = Math.max(0, Math.min(1, (now - startedAt) / actualDuration))
      const eased = easeInOut(progress)
      let x = from.x + (target.x - from.x) * eased
      let y = from.y + (target.y - from.y) * eased
      let rotation = 0
      let scale = 1
      if (mode === 'walk' && route && routeLength > 0 && routeStart && routeEnd) {
        const point = route.getPointAtLength(routeLength * eased)
        // Flow edges are drawn through control-node centers while actors stand
        // beside their stations. Interpolate the endpoint offsets so the walk
        // begins at the actor's real position and ends exactly at the target;
        // otherwise every step visibly jumps at the start/end of its route.
        x = point.x
          + (from.x - routeStart.x) * (1 - eased)
          + (target.x - routeEnd.x) * eased
        y = point.y
          + (from.y - routeStart.y) * (1 - eased)
          + (target.y - routeEnd.y) * eased
      } else if (mode === 'walk') {
        const distance = Math.hypot(target.x - from.x, target.y - from.y)
        y -= Math.sin(progress * Math.PI) * Math.min(90, distance * .1)
      } else if (mode === 'arc' || mode === 'merge') {
        y -= Math.sin(progress * Math.PI) * 80
        if (mode === 'merge') scale = 1 - eased * .45
      } else if (mode === 'drop') {
        y += Math.sin(progress * Math.PI * 4) * (1 - progress) * 10
      } else if (mode === 'spawn') {
        scale = .35 + eased * .65
        y -= Math.sin(progress * Math.PI) * 30
        element.style.opacity = String(Math.max(.12, eased))
      } else if (mode === 'sky') {
        rotation = eased * 420
        scale = 1 - eased * .3
      }
      element.setAttribute('transform', `translate(${x} ${y}) rotate(${rotation}) scale(${scale})`)
      this.positions.set(id, { x, y })
      if (progress < 1) {
        this.frames.set(id, requestAnimationFrame(render))
        return
      }
      element.setAttribute('transform', `translate(${target.x} ${target.y})`)
      element.style.opacity = '1'
      this.positions.set(id, target)
      this.frames.delete(id)
      if (hideAfter) this.show(id, false)
    }
    this.frames.set(id, requestAnimationFrame(render))
  }

  private setFlowStatus(event: WorkflowSimulationEvent) {
    const statusClass = isFailedStatus(event.status)
      ? 'is-failed'
      : isSucceededStatus(event.status)
        ? 'is-success'
        : 'is-active'
    if (event.type === 'actor.moved' || event.type === 'step.started')
      this.root()?.querySelectorAll('.flow-node.is-active, .flow-edge.is-active').forEach(item => item.classList.remove('is-active'))
    for (const id of [event.nodeId, event.edgeId, event.stationId]) {
      const item = this.find<SVGGraphicsElement>(id)
      if (!item) continue
      item.classList.remove('is-active', 'is-success', 'is-failed', 'is-unselected')
      item.classList.add(statusClass)
    }
  }

  private setActorStatus(id?: string, status?: WorkflowSimulationEvent['status']) {
    const actor = this.find<SVGGraphicsElement>(id)
    if (!actor) return
    actor.classList.remove('is-running', 'is-success', 'is-failed')
    if (normalizedStatus(status) === 'running') actor.classList.add('is-running')
    if (isSucceededStatus(status)) actor.classList.add('is-success')
    if (isFailedStatus(status)) actor.classList.add('is-failed')
  }

  private setTaskStatus(id?: string, status?: WorkflowSimulationEvent['status']) {
    const task = this.find<SVGGraphicsElement>(id)
    if (!task) return
    task.classList.remove('is-working', 'is-complete', 'is-failed')
    if (normalizedStatus(status) === 'running') task.classList.add('is-working')
    if (isSucceededStatus(status)) task.classList.add('is-complete')
    if (isFailedStatus(status)) task.classList.add('is-failed')
  }

  private pulseStation(id?: string, duration = 900) {
    const station = this.find<SVGGraphicsElement>(id)
    if (!station) return
    station.classList.add('is-active')
    window.setTimeout(() => station.classList.remove('is-active'), Math.max(200, Math.min(duration, 10_000)))
  }

  private animateRoundabout(id: string | undefined, duration: number) {
    const station = this.find<SVGGraphicsElement>(id)
    if (!station || window.matchMedia('(prefers-reduced-motion: reduce)').matches) return
    station.querySelectorAll<SVGGraphicsElement>('.roundabout-marking').forEach(marking => {
      const animation = marking.animate(
        [
          { strokeDashoffset: '0', opacity: .78 },
          { strokeDashoffset: '-92', opacity: 1 },
        ],
        {
          duration: 1400,
          iterations: Math.max(1, Math.ceil(duration / 1400)),
        },
      )
      this.stationAnimations.push(animation)
    })
  }

  private updateParcel(current?: number, total?: number, failed = false) {
    const parcel = this.find<SVGGraphicsElement>('task-root')
    if (!parcel) return
    if (failed) parcel.classList.add('is-failed')
    if (current === undefined || total === undefined || total <= 0) return
    const stamps = parcel.querySelectorAll<SVGGraphicsElement>('.parcel-stamp')
    stamps.forEach(stamp => {
      const index = Number(stamp.getAttribute('data-stamp-index') ?? 0)
      const threshold = Math.ceil(index / Math.max(1, stamps.length) * total)
      stamp.setAttribute('data-visible', threshold <= current ? 'true' : 'false')
    })
    const label = parcel.querySelector<SVGTextElement>('[data-part="parcel-progress"]')
    if (label) label.textContent = `Project parcel · ${Math.round(current / total * 100)}%`
  }
}
