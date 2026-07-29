export type GnouGnouWorkflowCharacterAction =
  | 'idle'
  | 'walk'
  | 'arrive'
  | 'pickup'
  | 'handoff'
  | 'type'
  | 'wait'
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
  status?: 'Pending' | 'Running' | 'Succeeded' | 'Failed' | 'Skipped'
  progressCurrent?: number
  progressTotal?: number
  x?: number
  y?: number
  message?: string
}

export interface WorkflowAnimationControllerOptions {
  onFocus?: (id: string, event: WorkflowSimulationEvent) => void
  onStatus?: (status: string, message?: string) => void
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
  path: SVGPathElement
  routingAnchor: Position
  inlet: Position
  outlet: Position
  activeTransferToken?: number
}
type MotionMode = 'walk' | 'arc' | 'drop' | 'spawn' | 'merge' | 'sky'

const SVG_NAMESPACE = 'http://www.w3.org/2000/svg'

function easeInOut(value: number): number {
  return value < .5
    ? 4 * value * value * value
    : 1 - Math.pow(-2 * value + 2, 3) / 2
}

function actionForStep(stepType?: string): GnouGnouWorkflowCharacterAction {
  const normalized = stepType?.toLowerCase() ?? ''
  if (normalized.startsWith('human.')) return 'wait'
  if (normalized === 'workflow.route') return 'communicate'
  if (normalized === 'workflow.plan') return 'type'
  if (normalized.startsWith('workflow.')) return 'handoff'
  if (normalized.startsWith('mcp.')) return 'communicate'
  return 'type'
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
    this.stationAnimations.splice(0).forEach(animation => animation.cancel())
    this.stopCameraMotion()
    this.characters.cancelAll()
    this.positions.clear()
    this.sceneLayers.clear()
    this.transitBranches.clear()
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
    this.stationAnimations.splice(0).forEach(animation => animation.cancel())
    this.characters.cancelAll()
    this.characters.stopAmbient()
    this.positions.clear()
    this.sceneLayers.clear()
    this.transitBranches.clear()
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
    if (this.liveEventTimer === undefined) this.playNextLiveEvent()
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
    root.setAttribute('aria-label', 'GnOuGo workflow transit pipes')
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

    const shell = document.createElementNS(SVG_NAMESPACE, 'path')
    shell.setAttribute('class', 'transit-pipe-shell')
    const core = document.createElementNS(SVG_NAMESPACE, 'path')
    core.setAttribute('class', 'transit-pipe-core')
    core.setAttribute('data-transit-path', 'true')
    const highlight = document.createElementNS(SVG_NAMESPACE, 'path')
    highlight.setAttribute('class', 'transit-pipe-highlight')
    const inlet = document.createElementNS(SVG_NAMESPACE, 'g')
    inlet.setAttribute('class', 'transit-pipe-mouth transit-pipe-inlet')
    inlet.innerHTML = '<circle r="54" class="transit-mouth-shell"/><circle r="43" class="transit-mouth-core"/>'
    const outlet = document.createElementNS(SVG_NAMESPACE, 'g')
    outlet.setAttribute('class', 'transit-pipe-mouth transit-pipe-outlet')
    outlet.innerHTML = '<circle r="60" class="transit-mouth-shell"/><circle r="48" class="transit-mouth-core"/><path d="M-14 -8L0 9L14 -8" class="transit-mouth-arrow"/>'
    const label = document.createElementNS(SVG_NAMESPACE, 'text')
    label.setAttribute('class', 'transit-pipe-label')
    label.textContent = event.workflowName ?? 'Routed workflow'
    group.append(shell, core, highlight, inlet, outlet, label)
    root.append(group)

    const branch: TransitBranch = {
      id: group.id,
      parentActorId: event.actorId,
      targetActorId: event.targetActorId,
      parentLaneId,
      targetLaneId,
      workflowName: event.workflowName ?? 'Routed workflow',
      group,
      path: core,
      routingAnchor: (event.stationId && this.find(event.stationId))
        ? this.readPosition(event.stationId)
        : (event.nodeId && this.find(event.nodeId))
          ? this.readPosition(event.nodeId)
          : this.readPosition(event.actorId),
      inlet: { x: 0, y: 0 },
      outlet: { x: 0, y: 0 },
    }
    this.transitBranches.set(`${event.actorId}->${event.targetActorId}`, branch)
    this.layoutTransitBranch(branch, false)
    this.promoteForeground(this.svgRoot()!)
    return branch
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
    return node?.id ? this.readPosition(node.id) : undefined
  }

  private layoutTransitBranch(branch: TransitBranch, reverse: boolean) {
    const siblings = Array.from(this.transitBranches.values())
      .filter(candidate => candidate.parentActorId === branch.parentActorId)
    const branchIndex = Math.max(0, siblings.indexOf(branch))
    const workflowAnchor = this.workflowControlPosition(
      branch.targetLaneId,
      reverse ? ['finish', 'return'] : ['start'],
    ) ?? this.readPosition(branch.targetActorId)
    const dx = workflowAnchor.x - branch.routingAnchor.x
    const dy = workflowAnchor.y - branch.routingAnchor.y
    const distance = Math.max(1, Math.hypot(dx, dy))
    const direction = { x: dx / distance, y: dy / distance }
    const routingClearance = Math.min(104, distance * .24)
    const workflowClearance = Math.min(72, distance * .18)
    const inlet = {
      x: branch.routingAnchor.x + direction.x * routingClearance,
      y: branch.routingAnchor.y + direction.y * routingClearance,
    }
    const outlet = {
      x: workflowAnchor.x - direction.x * workflowClearance,
      y: workflowAnchor.y - direction.y * workflowClearance,
    }
    branch.inlet = inlet
    branch.outlet = outlet

    const pipeDx = outlet.x - inlet.x
    const pipeDy = outlet.y - inlet.y
    const pipeDistance = Math.max(1, Math.hypot(pipeDx, pipeDy))
    const tangent = { x: pipeDx / pipeDistance, y: pipeDy / pipeDistance }
    const perpendicular = { x: -tangent.y, y: tangent.x }
    const branchSide = branchIndex % 2 === 0 ? 1 : -1
    const branchDepth = Math.floor(branchIndex / 2)
    const bow = Math.min(190, Math.max(58, pipeDistance * .16))
      * branchSide
      * (1 + branchDepth * .16)
    const controlDistance = pipeDistance * .32
    const firstControl = {
      x: inlet.x + tangent.x * controlDistance + perpendicular.x * bow,
      y: inlet.y + tangent.y * controlDistance + perpendicular.y * bow,
    }
    const secondControl = {
      x: outlet.x - tangent.x * controlDistance + perpendicular.x * bow,
      y: outlet.y - tangent.y * controlDistance + perpendicular.y * bow,
    }
    const path = `M ${inlet.x} ${inlet.y} C ${firstControl.x} ${firstControl.y} ${secondControl.x} ${secondControl.y} ${outlet.x} ${outlet.y}`
    branch.group.querySelectorAll<SVGPathElement>(
      '.transit-pipe-shell, .transit-pipe-core, .transit-pipe-highlight',
    ).forEach(element => element.setAttribute('d', path))
    branch.group.querySelector<SVGGElement>('.transit-pipe-inlet')
      ?.setAttribute('transform', `translate(${inlet.x} ${inlet.y})`)
    branch.group.querySelector<SVGGElement>('.transit-pipe-outlet')
      ?.setAttribute('transform', `translate(${outlet.x} ${outlet.y})`)
    const travelAngle = Math.atan2(
      reverse ? -pipeDy : pipeDy,
      reverse ? -pipeDx : pipeDx,
    ) * 180 / Math.PI
    branch.group.querySelector<SVGPathElement>('.transit-mouth-arrow')
      ?.setAttribute('transform', `rotate(${travelAngle - 90})`)
    const label = branch.group.querySelector<SVGTextElement>('.transit-pipe-label')
    label?.setAttribute('x', String((inlet.x + outlet.x) / 2 + perpendicular.x * bow))
    label?.setAttribute('y', String((inlet.y + outlet.y) / 2 + perpendicular.y * bow - 64))
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
        this.options.onStatus?.(
          event.status === 'Failed' ? 'Failed' : 'Running',
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
        this.playStepAction(event.actorId, 'wait', event.durationMs)
        this.pulseStation(event.stationId, Math.min(event.durationMs, 10_000))
        this.options.onStatus?.('Waiting for you', event.message)
        break
      case 'human_input.resumed':
        this.activateSceneForActor(event.actorId)
        this.stopPersistentAction(event.actorId)
        this.characters.play(event.actorId, 'pickup', Math.max(500, event.durationMs))
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
            // Outbound transfers connect the caller's routing roundabout to
            // the dynamic Start marker. Return transfers reuse the branch
            // from the child's Return marker back to that routing roundabout.
            this.layoutTransitBranch(transit.branch, transit.reverse)
            this.animateTransitActor(event, transit.branch, transit.reverse, targetPosition)
            this.animateTransitParcel(event, transit.branch, transit.reverse, targetPosition)
          } else {
            this.animateMotion(event.taskId, { x: targetPosition.x + 68, y: targetPosition.y - 82 }, event.durationMs, 'arc')
            this.activateSceneForActor(event.targetActorId)
          }
          const actionDuration = transit
            ? Math.max(350, this.transitDuration(event.durationMs))
            : Math.max(600, event.durationMs)
          this.characters.play(event.actorId, transit ? 'walk' : 'handoff', actionDuration, direction)
          this.characters.play(event.targetActorId, 'pickup', actionDuration, -direction)
        }
        if (event.status === 'Failed') this.setTaskStatus(event.taskId, 'Failed')
        break
      case 'step.started':
        this.activateSceneForActor(event.actorId)
        this.setActorStatus(event.actorId, 'Running')
        this.playStepAction(event.actorId, actionForStep(event.stepType), event.durationMs)
        this.pulseStation(event.stationId, Math.min(event.durationMs, 10_000))
        this.animateRoundabout(event.stationId, Math.min(event.durationMs, 60_000))
        break
      case 'step.completed':
        this.activateSceneForActor(event.actorId)
        this.stopPersistentAction(event.actorId)
        this.setActorStatus(event.actorId, event.status)
        this.updateParcel(event.progressCurrent, event.progressTotal, event.status === 'Failed')
        this.characters.play(
          event.actorId,
          event.status === 'Failed' ? 'fail' : 'celebrate',
          event.status === 'Failed' ? 1200 : 700,
        )
        break
      case 'output.sent':
        this.stopPersistentAction(event.actorId)
        if (event.x !== undefined && event.y !== undefined) {
          this.setTaskStatus(event.taskId, event.status)
          this.animateMotion(event.taskId, { x: event.x, y: event.y }, event.durationMs, 'sky', undefined, true)
          this.characters.play(event.actorId, event.status === 'Failed' ? 'fail' : 'deliver', Math.max(900, event.durationMs))
        }
        break
      case 'simulation.completed':
        this.stopPersistentAction(event.actorId)
        this.setActorStatus(event.actorId, event.status)
        this.characters.play(event.actorId, event.status === 'Failed' ? 'fail' : 'celebrate', 1600)
        this.options.onStatus?.(event.status === 'Failed' ? 'Failed' : 'Completed', event.message)
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
  }

  private playNextLiveEvent() {
    const event = this.liveEventQueue.shift()
    if (!event) {
      this.liveEventTimer = undefined
      this.setHostDiagnostic('data-animation-queued-events', '0')
      return
    }

    this.setHostDiagnostic('data-animation-queued-events', String(this.liveEventQueue.length))
    try {
      this.applyEvent(event)
      this.appliedEventCount += 1
      this.setHostDiagnostic('data-animation-state', 'playing')
      this.setHostDiagnostic('data-animation-event-count', String(this.appliedEventCount))
      this.setHostDiagnostic('data-animation-last-event', event.type)
      this.setHostDiagnostic('data-animation-error', '')
    } catch (error) {
      const message = error instanceof Error ? error.message : String(error)
      this.setHostDiagnostic('data-animation-state', 'recovering')
      this.setHostDiagnostic('data-animation-error', message)
      console.error('[GnOuGo.Animation] Could not apply live workflow event.', event, error)
    } finally {
      this.liveEventTimer = window.setTimeout(() => {
        this.liveEventTimer = undefined
        this.playNextLiveEvent()
      }, this.livePresentationGap(event))
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
        return Math.max(260, Math.min(event.durationMs * .68, 650))
      case 'step.started':
        return 320
      case 'step.completed':
        return 360
      case 'output.sent':
        return 650
      default:
        return 80
    }
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
    const destination = this.focusDestinationForEvent(event)
    this.focus(focusId, behavior, destination, event.durationMs)
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
    const collapsedStep = (event.type === 'step.started' || event.type === 'step.completed')
      && !event.targetNodeId
      && !event.stationId
      && !event.nodeId
    if (collapsedStep) return undefined
    if (event.type === 'task.handed_off') {
      const transit = this.findTransitBranch(event.actorId, event.targetActorId)
      return transit?.branch.id ?? event.targetActorId ?? event.actorId
    }
    if (event.type === 'actor.cloned'
      || event.type === 'actor.merged')
      return event.targetActorId ?? event.actorId
    return event.targetNodeId ?? event.stationId ?? event.nodeId ?? event.actorId
  }

  private focusDestinationForEvent(event: WorkflowSimulationEvent): Position | undefined {
    if (event.x === undefined || event.y === undefined) return undefined
    return event.type === 'actor.moved' || event.type === 'actor.spawned'
      ? { x: event.x, y: event.y }
      : undefined
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
      : Math.max(180, Math.min(680, requestedDurationMs))
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
    return Math.max(180, Math.min(durationMs * .58, 1200))
  }

  private animateTransitActor(
    event: WorkflowSimulationEvent,
    branch: TransitBranch,
    reverse: boolean,
    targetActorPosition: Position,
  ) {
    if (!event.actorId) return
    const actor = this.find<SVGGraphicsElement>(event.actorId)
    const transitRoot = this.find<SVGGElement>('gnougo-transit-actors')
    if (!actor || !transitRoot || !actor.parentNode) return
    let routeLength = 0
    try {
      routeLength = branch.path.getTotalLength()
    } catch {
      return
    }
    if (routeLength <= 0) return

    const previous = this.frames.get(event.actorId)
    if (previous !== undefined) cancelAnimationFrame(previous)
    const originalParent = actor.parentNode
    const originalNextSibling = actor.nextSibling
    const originalTransform = actor.getAttribute('transform')
    const originalStyleOpacity = actor.style.opacity
    const originalPosition = this.readPosition(event.actorId)
    const pipeStart = reverse ? branch.outlet : branch.inlet
    const pipeEnd = reverse ? branch.inlet : branch.outlet
    const destination = {
      x: targetActorPosition.x + (reverse ? 104 : -104),
      y: targetActorPosition.y,
    }
    const duration = this.transitDuration(event.durationMs)
    const startedAt = performance.now()
    const generation = this.generation
    actor.classList.add('is-in-transit')
    transitRoot.append(actor)
    actor.style.opacity = '1'

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
      actor.classList.remove('is-in-transit')
      this.positions.set(event.actorId!, originalPosition)
      this.frames.delete(event.actorId!)
    }

    const render = (now: number) => {
      if (generation !== this.generation || !actor.isConnected) return
      const progress = Math.max(0, Math.min(1, (now - startedAt) / duration))
      const eased = easeInOut(progress)
      let position: Position
      let scale: number
      let rotation = 0
      if (eased < .16) {
        const local = easeInOut(eased / .16)
        position = this.interpolatePosition(originalPosition, pipeStart, local)
        position.y -= Math.sin(local * Math.PI) * 24
        scale = 1 - local * .58
      } else if (eased < .84) {
        const pipeProgress = (eased - .16) / .68
        const length = reverse
          ? routeLength * (1 - pipeProgress)
          : routeLength * pipeProgress
        const point = branch.path.getPointAtLength(length)
        position = { x: point.x, y: point.y }
        scale = .30 + Math.abs(pipeProgress - .5) * .24
        rotation = (reverse ? -1 : 1) * Math.sin(pipeProgress * Math.PI) * 9
      } else {
        const local = easeInOut((eased - .84) / .16)
        position = this.interpolatePosition(pipeEnd, destination, local)
        position.y -= Math.sin(local * Math.PI) * 24
        scale = .42 + local * .58
      }
      actor.setAttribute(
        'transform',
        `translate(${position.x} ${position.y}) rotate(${rotation}) scale(${scale})`,
      )
      this.positions.set(event.actorId!, position)
      if (progress < 1) {
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
    targetActorPosition: Position,
  ) {
    if (!event.taskId) {
      this.activateSceneForActor(event.targetActorId, reverse ? 'reverse' : 'forward')
      return
    }
    let routeLength = 0
    try {
      routeLength = branch.path.getTotalLength()
    } catch {
      // Older embedded webviews can expose SVGPathElement without geometry APIs.
    }
    if (routeLength <= 0) {
      this.animateMotion(
        event.taskId,
        { x: targetActorPosition.x + 68, y: targetActorPosition.y - 82 },
        event.durationMs,
        'arc',
      )
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
      x: targetActorPosition.x + 68,
      y: targetActorPosition.y - 82,
    }
    const pipeStart = reverse ? branch.outlet : branch.inlet
    const pipeEnd = reverse ? branch.inlet : branch.outlet
    const duration = this.transitDuration(event.durationMs)
    const startedAt = performance.now()
    const generation = this.generation
    let sceneChanged = false
    const transferToken = ++this.transitTransferSequence
    branch.activeTransferToken = transferToken
    branch.group.classList.add('is-active')
    branch.group.classList.toggle('is-returning', reverse)
    branch.group.setAttribute('data-transit-direction', reverse ? 'return' : 'outbound')
    this.show(visualId, true)

    const render = (now: number) => {
      if (generation !== this.generation || !parcel.isConnected) return
      const progress = Math.max(0, Math.min(1, (now - startedAt) / duration))
      const eased = easeInOut(progress)
      let position: Position
      let pipeProgress = 0
      if (eased < .18) {
        const local = easeInOut(eased / .18)
        position = this.interpolatePosition(from, pipeStart, local)
        position.y -= Math.sin(local * Math.PI) * 34
      } else if (eased < .82) {
        pipeProgress = (eased - .18) / .64
        const length = reverse
          ? routeLength * (1 - pipeProgress)
          : routeLength * pipeProgress
        const point = branch.path.getPointAtLength(length)
        position = { x: point.x, y: point.y }
      } else {
        const local = easeInOut((eased - .82) / .18)
        position = this.interpolatePosition(pipeEnd, destination, local)
        position.y -= Math.sin(local * Math.PI) * 34
        pipeProgress = 1
      }
      const insidePipe = eased >= .18 && eased <= .82
      const scale = insidePipe
        ? .34 + Math.abs(pipeProgress - .5) * .36
        : .7 + Math.abs(eased - .5) * .6
      const rotation = (reverse ? -1 : 1) * eased * 420
      parcel.setAttribute(
        'transform',
        `translate(${position.x} ${position.y}) rotate(${rotation}) scale(${Math.min(1, scale)})`,
      )
      this.positions.set(visualId, position)

      const sceneSwitchProgress = reverse ? .22 : .46
      if (!sceneChanged && progress >= sceneSwitchProgress) {
        sceneChanged = true
        this.activateSceneForActor(event.targetActorId, reverse ? 'reverse' : 'forward')
      }
      if (progress < 1) {
        this.frames.set(visualId, requestAnimationFrame(render))
        return
      }

      this.frames.delete(visualId)
      if (branch.activeTransferToken === transferToken) {
        branch.activeTransferToken = undefined
        branch.group.classList.remove('is-active', 'is-returning')
        branch.group.removeAttribute('data-transit-direction')
      }
      parcel.setAttribute('transform', `translate(${destination.x} ${destination.y})`)
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
    this.show(id, true)

    const render = (now: number) => {
      if (generation !== this.generation || !element.isConnected) return
      const progress = Math.max(0, Math.min(1, (now - startedAt) / actualDuration))
      const eased = easeInOut(progress)
      let x = from.x + (target.x - from.x) * eased
      let y = from.y + (target.y - from.y) * eased
      let rotation = 0
      let scale = 1
      if (mode === 'walk' && route && routeLength > 0) {
        const point = route.getPointAtLength(routeLength * eased)
        x = point.x
        y = point.y
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
    const statusClass = event.status === 'Failed'
      ? 'is-failed'
      : event.status === 'Succeeded'
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

  private setActorStatus(id?: string, status?: string) {
    const actor = this.find<SVGGraphicsElement>(id)
    if (!actor) return
    actor.classList.remove('is-running', 'is-success', 'is-failed')
    if (status === 'Running') actor.classList.add('is-running')
    if (status === 'Succeeded') actor.classList.add('is-success')
    if (status === 'Failed') actor.classList.add('is-failed')
  }

  private setTaskStatus(id?: string, status?: string) {
    const task = this.find<SVGGraphicsElement>(id)
    if (!task) return
    task.classList.remove('is-working', 'is-complete', 'is-failed')
    if (status === 'Running') task.classList.add('is-working')
    if (status === 'Succeeded') task.classList.add('is-complete')
    if (status === 'Failed') task.classList.add('is-failed')
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
