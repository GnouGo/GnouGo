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
  onFocus?: (id: string) => void
  onStatus?: (status: string, message?: string) => void
}

interface Position { x: number; y: number }
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
  private readonly deskAnimations: Animation[] = []
  private readonly liveEventQueue: WorkflowSimulationEvent[] = []
  private readonly persistentActionTimers = new Map<string, number>()
  private liveEventTimer: number | undefined
  private appliedEventCount = 0
  private generation = 0

  constructor(
    private readonly root: () => HTMLElement | null,
    private readonly characters: GnouGnouWorkflowCharacterController,
    private readonly options: WorkflowAnimationControllerOptions = {},
  ) {}

  attach() {
    this.positions.clear()
    this.appliedEventCount = 0
    this.setHostDiagnostic('data-animation-state', 'attached')
    this.setHostDiagnostic('data-animation-event-count', '0')
    this.setHostDiagnostic('data-animation-last-event', '')
    this.setHostDiagnostic('data-animation-error', '')
    this.characters.startAmbient()
  }

  dispose() {
    this.generation += 1
    if (this.liveEventTimer !== undefined) window.clearTimeout(this.liveEventTimer)
    this.liveEventTimer = undefined
    this.liveEventQueue.length = 0
    this.persistentActionTimers.forEach(timer => window.clearTimeout(timer))
    this.persistentActionTimers.clear()
    this.frames.forEach(frame => cancelAnimationFrame(frame))
    this.frames.clear()
    this.deskAnimations.splice(0).forEach(animation => animation.cancel())
    this.characters.cancelAll()
    this.characters.stopAmbient()
    this.positions.clear()
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
    const parser = new DOMParser()
    const documentNode = parser.parseFromString(
      `<svg xmlns="${SVG_NAMESPACE}">${patch.svgFragment}</svg>`,
      'image/svg+xml',
    )
    const parsedRoot = documentNode.documentElement
    while (parsedRoot.firstChild)
      svg.append(document.importNode(parsedRoot.firstChild, true))
    svg.setAttribute('viewBox', `0 0 ${patch.bounds.width} ${patch.bounds.height}`)
    svg.setAttribute('width', String(patch.bounds.width))
    svg.setAttribute('height', String(patch.bounds.height))
    this.options.onStatus?.('Running', 'A runtime workflow joined the scene.')
  }

  applyEvent(event: WorkflowSimulationEvent) {
    const actorPosition = event.actorId ? this.readPosition(event.actorId) : undefined
    const targetPosition = event.targetActorId ? this.readPosition(event.targetActorId) : undefined
    this.setFlowStatus(event)

    switch (event.type) {
      case 'simulation.started':
        this.options.onStatus?.('Running', event.message)
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
        this.playStepAction(event.actorId, 'wait', event.durationMs)
        this.pulseStation(event.stationId, Math.min(event.durationMs, 10_000))
        this.options.onStatus?.('Waiting for you', event.message)
        break
      case 'human_input.resumed':
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
          this.animateMotion(event.taskId, { x: targetPosition.x + 68, y: targetPosition.y - 82 }, event.durationMs, 'arc')
          this.characters.play(event.actorId, 'handoff', Math.max(600, event.durationMs), direction)
          this.characters.play(event.targetActorId, 'pickup', Math.max(600, event.durationMs), -direction)
        }
        if (event.status === 'Failed') this.setTaskStatus(event.taskId, 'Failed')
        break
      case 'step.started':
        this.setActorStatus(event.actorId, 'Running')
        this.playStepAction(event.actorId, actionForStep(event.stepType), event.durationMs)
        this.pulseStation(event.stationId, Math.min(event.durationMs, 10_000))
        this.animateDesk(event.stationId, Math.min(event.durationMs, 60_000))
        break
      case 'step.completed':
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

    const statusText = this.find<SVGTextElement>('simulation-status')
    if (statusText && event.message) statusText.textContent = event.message
    const focusId = event.targetNodeId ?? event.stationId ?? event.nodeId ?? event.actorId
    if (focusId) this.options.onFocus?.(focusId)
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
      case 'actor.cloned':
      case 'actor.merged':
        return 380
      case 'task.handed_off':
        return 520
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

  focus(id: string, behavior: ScrollBehavior = 'smooth') {
    const host = this.root()
    const element = this.find<SVGGraphicsElement>(id)
    if (!host || !element) return
    const resolvedBehavior = window.matchMedia('(prefers-reduced-motion: reduce)').matches ? 'auto' : behavior
    const hasInternalViewport = host.scrollHeight > host.clientHeight + 2
      || host.scrollWidth > host.clientWidth + 2
    if (!hasInternalViewport) {
      element.scrollIntoView({
        behavior: resolvedBehavior,
        block: 'center',
        inline: 'center',
      })
      return
    }

    const hostRect = host.getBoundingClientRect()
    const elementRect = element.getBoundingClientRect()
    host.scrollTo({
      left: Math.max(0, host.scrollLeft + elementRect.left - hostRect.left - host.clientWidth / 2 + elementRect.width / 2),
      top: Math.max(0, host.scrollTop + elementRect.top - hostRect.top - host.clientHeight / 2 + elementRect.height / 2),
      behavior: resolvedBehavior,
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

  private animateDesk(id: string | undefined, duration: number) {
    const station = this.find<SVGGraphicsElement>(id)
    if (!station || window.matchMedia('(prefers-reduced-motion: reduce)').matches) return
    station.querySelectorAll<SVGGraphicsElement>('[data-key]').forEach((key, index) => {
      const animation = key.animate(
        [
          { transform: 'translateY(0)', fill: '#d9e7ef' },
          { transform: 'translateY(2px)', fill: index % 2 === 0 ? '#72e8d0' : '#fff47b' },
          { transform: 'translateY(0)', fill: '#d9e7ef' },
        ],
        {
          duration: 150 + index % 4 * 20,
          delay: index % 9 * 20,
          iterations: Math.max(1, Math.ceil(duration / 180)),
        },
      )
      this.deskAnimations.push(animation)
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
