import { memo, useCallback, useEffect, useMemo, useRef, useState, type PointerEvent as ReactPointerEvent, type WheelEvent as ReactWheelEvent } from 'react'
import {
  GnouGnouAnimationController,
  type GnouGnouAnimationName,
} from '../../../GnOuGo.Assets.Bears/Runtime/gnougnou-animation-controller'
import {
  GnouGnouWorkflowAnimationController,
} from '../../../GnOuGo.Assets.Animation/Runtime/gnougnou-workflow-animation-controller'
import { streamSimulation, validatePreview } from './api'
import { EXAMPLE_INPUTS, EXAMPLE_WORKFLOW } from './constants'
import type {
  FailureTarget,
  FeedItem,
  SceneKind,
  SimulationEvent,
  SimulationPrepared,
  SimulationRequest,
  StreamEnvelope,
  ValidationResponse,
} from './types'

interface Position { x: number; y: number }
type MotionMode = 'walk' | 'arc' | 'drop' | 'work' | 'spawn' | 'merge' | 'complete' | 'sky'

interface MotionOptions {
  mode?: MotionMode
  arc?: number
  hideAfter?: boolean
  pathId?: string
}

const SVG_NAMESPACE = 'http://www.w3.org/2000/svg'

const scenes: SceneKind[] = ['Random', 'Office', 'Meadow', 'Kitchen']
const speeds = [0.5, 1, 2, 4]

function parseInputs(value: string): unknown {
  if (!value.trim()) return {}
  const parsed: unknown = JSON.parse(value)
  if (parsed === null || typeof parsed !== 'object' || Array.isArray(parsed)) {
    throw new Error('Inputs must be a JSON object.')
  }
  return parsed
}

function failureValue(target: FailureTarget): string {
  return JSON.stringify({ workflowName: target.workflowName, stepId: target.stepId })
}

function nextFrame(): Promise<void> {
  return new Promise(resolve => requestAnimationFrame(() => resolve()))
}

function easeInOut(value: number): number {
  return value < 0.5 ? 4 * value * value * value : 1 - Math.pow(-2 * value + 2, 3) / 2
}

function actionForStep(stepType?: string): GnouGnouAnimationName {
  const normalized = stepType?.toLowerCase() ?? ''
  if (normalized.startsWith('workflow.')) return 'handoff'
  return 'type'
}

const MotionSvgScene = memo(function MotionSvgScene({
  svg,
  width,
  height,
  zoom,
}: {
  svg: string
  width: number
  height: number
  zoom: number
}) {
  return (
    <div
      className="svg-frame"
      aria-label="GnOuGo workflow simulation"
      style={{ width: `${width * zoom}px`, height: `${height * zoom}px` }}
      dangerouslySetInnerHTML={{ __html: svg }}
    />
  )
})

export default function App() {
  const [workflow, setWorkflow] = useState(EXAMPLE_WORKFLOW)
  const [inputs, setInputs] = useState(EXAMPLE_INPUTS)
  const [scene, setScene] = useState<SceneKind>('Random')
  const [seed, setSeed] = useState('42')
  const [speed, setSpeed] = useState(1)
  const [failAt, setFailAt] = useState('')
  const [validation, setValidation] = useState<ValidationResponse | null>(null)
  const [validationBusy, setValidationBusy] = useState(false)
  const [inputError, setInputError] = useState<string | null>(null)
  const [runError, setRunError] = useState<string | null>(null)
  const [running, setRunning] = useState(false)
  const [prepared, setPrepared] = useState<SimulationPrepared | null>(null)
  const [svg, setSvg] = useState('')
  const [feed, setFeed] = useState<FeedItem[]>([])
  const [runStatus, setRunStatus] = useState('Ready')
  const [zoom, setZoom] = useState(1)
  const [autoFollow, setAutoFollow] = useState(true)
  const abortRef = useRef<AbortController | null>(null)
  const autoRunRef = useRef(false)
  const svgHostRef = useRef<HTMLDivElement>(null)
  const gnouGnouAnimationsRef = useRef<GnouGnouAnimationController | null>(null)
  if (gnouGnouAnimationsRef.current === null)
    gnouGnouAnimationsRef.current = new GnouGnouAnimationController(() => svgHostRef.current)
  const workflowAnimationsRef = useRef<GnouGnouWorkflowAnimationController | null>(null)
  if (workflowAnimationsRef.current === null)
    workflowAnimationsRef.current = new GnouGnouWorkflowAnimationController(
      () => svgHostRef.current,
      gnouGnouAnimationsRef.current,
    )
  const positionsRef = useRef(new Map<string, Position>())
  const animationsRef = useRef(new Map<string, number>())
  const deskAnimationsRef = useRef<Animation[]>([])
  const autoFollowRef = useRef(true)
  const lastFocusRef = useRef<string | null>(null)
  const dragRef = useRef<{ x: number; y: number; left: number; top: number } | null>(null)
  const motionGenerationRef = useRef(0)

  const buildRequest = useCallback((includeFailure: boolean): SimulationRequest => {
    const parsedSeed = Number.parseInt(seed, 10)
    const request: SimulationRequest = {
      workflow,
      inputs: parseInputs(inputs),
      seed: Number.isFinite(parsedSeed) ? parsedSeed : undefined,
      scene,
      speed,
    }
    if (includeFailure && failAt) request.failAt = JSON.parse(failAt) as { workflowName: string; stepId: string }
    return request
  }, [failAt, inputs, scene, seed, speed, workflow])

  useEffect(() => {
    autoFollowRef.current = autoFollow
  }, [autoFollow])

  useEffect(() => {
    const controller = new AbortController()
    const timer = window.setTimeout(async () => {
      setValidationBusy(true)
      try {
        const request = buildRequest(false)
        setInputError(null)
        setValidation(await validatePreview(request, controller.signal))
      } catch (error) {
        if (controller.signal.aborted) return
        const message = error instanceof Error ? error.message : String(error)
        if (message.toLowerCase().includes('json')) setInputError(message)
        else setValidation({ valid: false, diagnostics: [{ code: 'REQUEST', message, severity: 'Error' }], failureTargets: [], workflows: [] })
      } finally {
        if (!controller.signal.aborted) setValidationBusy(false)
      }
    }, 500)
    return () => {
      window.clearTimeout(timer)
      controller.abort()
    }
  }, [buildRequest])

  const findElement = useCallback((id?: string): SVGGraphicsElement | null => {
    if (!id) return null
    return svgHostRef.current?.querySelector<SVGGraphicsElement>(`#${CSS.escape(id)}`) ?? null
  }, [])

  const readPosition = useCallback((id: string): Position => {
    const known = positionsRef.current.get(id)
    if (known) return known
    const transform = findElement(id)?.getAttribute('transform') ?? ''
    const match = /translate\((-?[\d.]+)[ ,](-?[\d.]+)\)/.exec(transform)
    const position = match ? { x: Number(match[1]), y: Number(match[2]) } : { x: 0, y: 0 }
    positionsRef.current.set(id, position)
    return position
  }, [findElement])

  const showElement = useCallback((id?: string, visible = true) => {
    const element = findElement(id)
    if (!element) return
    element.setAttribute('data-visible', visible ? 'true' : 'false')
    element.style.opacity = visible ? '1' : '0'
  }, [findElement])

  const setPosition = useCallback((id: string, position: Position, rotation = 0, scale = 1) => {
    const element = findElement(id)
    if (!element) return
    element.style.transform = ''
    element.setAttribute('transform', `translate(${position.x} ${position.y}) rotate(${rotation}) scale(${scale})`)
    positionsRef.current.set(id, position)
  }, [findElement])

  const cancelMotions = useCallback(() => {
    motionGenerationRef.current += 1
    animationsRef.current.forEach(frame => cancelAnimationFrame(frame))
    animationsRef.current.clear()
    deskAnimationsRef.current.forEach(animation => animation.cancel())
    deskAnimationsRef.current = []
    gnouGnouAnimationsRef.current?.cancelAll()
  }, [])

  const animateCharacterPose = useCallback((
    actorId: string | undefined,
    action: GnouGnouAnimationName,
    duration: number,
    direction = 1,
  ) => {
    gnouGnouAnimationsRef.current?.play(actorId, action, duration, direction)
  }, [])

  const animateMotion = useCallback((
    id: string | undefined,
    target: Position,
    duration: number,
    options: MotionOptions = {},
  ) => {
    if (!id) return
    const element = findElement(id)
    if (!element) return
    const previousFrame = animationsRef.current.get(id)
    if (previousFrame !== undefined) cancelAnimationFrame(previousFrame)

    const mode = options.mode ?? 'arc'
    const from = readPosition(id)
    const reduced = window.matchMedia('(prefers-reduced-motion: reduce)').matches
    const actualDuration = reduced ? 1 : Math.max(16, duration)
    const generation = motionGenerationRef.current
    const startedAt = performance.now()
    const arc = options.arc ?? Math.min(130, Math.max(38, Math.hypot(target.x - from.x, target.y - from.y) * 0.18))
    const route = options.pathId
      ? findElement(options.pathId)?.querySelector<SVGPathElement>('[data-route-path="true"]') ?? null
      : null
    const routeLength = route?.getTotalLength() ?? 0
    const routeStart = route && routeLength > 0 ? route.getPointAtLength(0) : null
    const routeEnd = route && routeLength > 0 ? route.getPointAtLength(routeLength) : null
    const followsRoute = mode === 'walk'
      && route !== null
      && routeStart !== null
      && routeEnd !== null
      && Math.hypot(from.x - routeStart.x, from.y - routeStart.y) < 110
      && Math.hypot(target.x - routeEnd.x, target.y - routeEnd.y) < 30

    showElement(id, true)
    element.style.willChange = 'transform, opacity'

    const render = (now: number) => {
      if (generation !== motionGenerationRef.current || !element.isConnected) return
      const progress = Math.max(0, Math.min(1, (now - startedAt) / actualDuration))
      const eased = easeInOut(progress)
      let x = from.x + (target.x - from.x) * eased
      let y = from.y + (target.y - from.y) * eased
      let rotation = 0
      let scale = 1

      if (followsRoute && route) {
        const routePoint = route.getPointAtLength(routeLength * eased)
        x = routePoint.x
        y = routePoint.y
      } else if (mode === 'walk') {
        // Locomotion stays on a stable interpolated path. The articulated rig
        // supplies the footsteps, avoiding a vibrating whole-character sprite.
      } else if (mode === 'arc' || mode === 'merge') {
        y -= Math.sin(progress * Math.PI) * arc
        rotation = Math.sin(progress * Math.PI) * (mode === 'merge' ? 18 : 8)
        if (mode === 'merge') scale = 1 - eased * 0.45
      } else if (mode === 'drop') {
        y += Math.sin(progress * Math.PI * 4) * (1 - progress) * 13
        rotation = Math.sin(progress * Math.PI * 3) * (1 - progress) * 8
      } else if (mode === 'work') {
        const activity = Math.sin(progress * Math.PI * 10)
        x += activity * 5
        y -= Math.abs(activity) * 9
        rotation = activity * 5
        scale = 1 + Math.abs(activity) * 0.045
      } else if (mode === 'spawn') {
        scale = 0.35 + eased * 0.65
        y -= Math.sin(progress * Math.PI) * 32
        element.style.opacity = String(Math.max(0.12, eased))
      } else if (mode === 'complete') {
        y -= Math.sin(progress * Math.PI) * arc
        rotation = eased * 270
        scale = 1 - eased * 0.35
        element.style.opacity = String(1 - Math.max(0, progress - 0.55) / 0.45)
      } else if (mode === 'sky') {
        x += Math.sin(progress * Math.PI * 2) * 16
        rotation = eased * 540
        scale = 1 - eased * 0.35
      }

      element.setAttribute('transform', `translate(${x} ${y}) rotate(${rotation}) scale(${scale})`)
      positionsRef.current.set(id, { x, y })

      if (progress < 1) {
        const frame = requestAnimationFrame(render)
        animationsRef.current.set(id, frame)
        return
      }

      element.setAttribute('transform', `translate(${target.x} ${target.y})`)
      element.style.willChange = ''
      element.style.opacity = '1'
      positionsRef.current.set(id, target)
      animationsRef.current.delete(id)
      if (options.hideAfter) showElement(id, false)
    }

    const frame = requestAnimationFrame(render)
    animationsRef.current.set(id, frame)
  }, [findElement, readPosition, showElement])

  const drawMotionTrail = useCallback((from: Position, to: Position, duration: number, color: string) => {
    if (window.matchMedia('(prefers-reduced-motion: reduce)').matches) return
    const layer = svgHostRef.current?.querySelector<SVGGElement>('#motion-trails')
    if (!layer) return
    const path = document.createElementNS(SVG_NAMESPACE, 'path')
    const curve = Math.min(90, Math.max(30, Math.abs(to.x - from.x) * 0.1))
    path.setAttribute('d', `M ${from.x} ${from.y - 38} Q ${(from.x + to.x) / 2} ${Math.min(from.y, to.y) - curve} ${to.x} ${to.y - 38}`)
    path.setAttribute('fill', 'none')
    path.setAttribute('stroke', color)
    path.setAttribute('stroke-width', '6')
    path.setAttribute('stroke-linecap', 'round')
    path.setAttribute('stroke-dasharray', '13 14')
    path.setAttribute('opacity', '.7')
    layer.append(path)
    const animation = path.animate(
      [{ opacity: 0, strokeDashoffset: '90' }, { opacity: .72, offset: .18 }, { opacity: 0, strokeDashoffset: '0' }],
      { duration: Math.max(180, duration + 280), easing: 'ease-out' },
    )
    animation.onfinish = () => path.remove()
  }, [])

  const pulseStation = useCallback((stationId?: string, duration = 900) => {
    const station = findElement(stationId)
    if (!station) return
    station.classList.add('is-active')
    window.setTimeout(() => station.classList.remove('is-active'), Math.max(200, duration))
  }, [findElement])

  const animateDesk = useCallback((stationId: string | undefined, duration: number) => {
    const station = findElement(stationId)
    if (!station || window.matchMedia('(prefers-reduced-motion: reduce)').matches) return
    const keys = [...station.querySelectorAll<SVGGraphicsElement>('[data-key]')]
    keys.forEach((key, index) => {
      const animation = key.animate(
        [
          { transform: 'translateY(0)', fill: '#d9e7ef' },
          { transform: 'translateY(2px)', fill: index % 2 === 0 ? '#72e8d0' : '#fff47b' },
          { transform: 'translateY(0)', fill: '#d9e7ef' },
        ],
        {
          duration: 125 + index % 4 * 18,
          delay: index % 9 * 17,
          iterations: Math.max(1, Math.ceil(duration / 150)),
        },
      )
      deskAnimationsRef.current.push(animation)
    })
    station.querySelectorAll<SVGGraphicsElement>('.desk-screen-line').forEach((line, index) => {
      const animation = line.animate(
        [{ opacity: .35 }, { opacity: 1 }, { opacity: .55 }],
        {
          duration: 260 + index * 80,
          iterations: Math.max(1, Math.ceil(duration / 320)),
          direction: 'alternate',
        },
      )
      deskAnimationsRef.current.push(animation)
    })
  }, [findElement])

  const setFlowStatus = useCallback((event: SimulationEvent) => {
    const isCollapsedStep = (event.type === 'step.started' || event.type === 'step.completed')
      && !event.nodeId
      && !event.edgeId
      && !event.stationId
    if (isCollapsedStep) return
    const statusClass = event.status === 'Failed'
      ? 'is-failed'
      : event.status === 'Succeeded'
        ? 'is-success'
        : 'is-active'
    if (event.type === 'actor.moved' || event.type === 'step.started') {
      svgHostRef.current?.querySelectorAll('.flow-node.is-active, .flow-edge.is-active').forEach(element => element.classList.remove('is-active'))
    }
    for (const id of [event.nodeId, event.edgeId, event.stationId]) {
      const element = findElement(id)
      if (!element) continue
      element.classList.remove('is-active', 'is-success', 'is-failed')
      element.classList.add(statusClass)
    }
  }, [findElement])

  const updateParcelProgress = useCallback((current?: number, total?: number, failed = false) => {
    const parcel = findElement('task-root')
    if (!parcel) return
    if (failed) {
      parcel.classList.add('is-failed')
      parcel.classList.remove('is-complete')
    }
    if (current === undefined || total === undefined || total <= 0) return
    parcel.querySelectorAll<SVGGraphicsElement>('.parcel-stamp').forEach(stamp => {
      const index = Number(stamp.getAttribute('data-stamp-index') ?? 0)
      const visibleThreshold = Math.ceil(index / Math.max(1, parcel.querySelectorAll('.parcel-stamp').length) * total)
      stamp.setAttribute('data-visible', visibleThreshold <= current ? 'true' : 'false')
    })
    const text = parcel.querySelector<SVGTextElement>('[data-part="parcel-progress"]')
    if (text) text.textContent = `Project parcel · ${Math.round(current / total * 100)}%`
  }, [findElement])

  const focusElement = useCallback((id?: string, force = false) => {
    if (!id || (!force && !autoFollowRef.current)) return
    const viewport = svgHostRef.current
    const element = findElement(id)
    if (!viewport || !element) return
    lastFocusRef.current = id
    const viewportRect = viewport.getBoundingClientRect()
    const elementRect = element.getBoundingClientRect()
    const targetLeft = viewport.scrollLeft + elementRect.left - viewportRect.left - viewport.clientWidth / 2 + elementRect.width / 2
    const targetTop = viewport.scrollTop + elementRect.top - viewportRect.top - viewport.clientHeight / 2 + elementRect.height / 2
    viewport.scrollTo({
      left: Math.max(0, targetLeft),
      top: Math.max(0, targetTop),
      behavior: window.matchMedia('(prefers-reduced-motion: reduce)').matches ? 'auto' : 'smooth',
    })
  }, [findElement])

  const fitCanvas = useCallback(() => {
    const viewport = svgHostRef.current
    if (!viewport || !prepared) return
    const nextZoom = Math.max(.28, Math.min(1, (viewport.clientWidth - 24) / prepared.canvasWidth))
    setZoom(nextZoom)
    window.setTimeout(() => focusElement(lastFocusRef.current ?? 'node-workflow-master-start', true), 30)
  }, [focusElement, prepared])

  const handleStagePointerDown = useCallback((event: ReactPointerEvent<HTMLDivElement>) => {
    if (event.button !== 0) return
    const viewport = event.currentTarget
    dragRef.current = {
      x: event.clientX,
      y: event.clientY,
      left: viewport.scrollLeft,
      top: viewport.scrollTop,
    }
    viewport.setPointerCapture(event.pointerId)
    setAutoFollow(false)
  }, [])

  const handleStagePointerMove = useCallback((event: ReactPointerEvent<HTMLDivElement>) => {
    const drag = dragRef.current
    if (!drag) return
    event.currentTarget.scrollLeft = drag.left - (event.clientX - drag.x)
    event.currentTarget.scrollTop = drag.top - (event.clientY - drag.y)
  }, [])

  const handleStagePointerUp = useCallback((event: ReactPointerEvent<HTMLDivElement>) => {
    dragRef.current = null
    if (event.currentTarget.hasPointerCapture(event.pointerId)) event.currentTarget.releasePointerCapture(event.pointerId)
  }, [])

  const handleStageWheel = useCallback((event: ReactWheelEvent<HTMLDivElement>) => {
    if (event.ctrlKey || event.metaKey) {
      event.preventDefault()
      setZoom(current => Math.max(.28, Math.min(1.6, current * (event.deltaY > 0 ? .9 : 1.1))))
    }
    setAutoFollow(false)
  }, [])

  useEffect(() => () => {
    abortRef.current?.abort()
    cancelMotions()
    workflowAnimationsRef.current?.dispose()
  }, [cancelMotions])

  const setTaskStatus = useCallback((taskId: string | undefined, status?: string) => {
    const task = findElement(taskId)
    if (!task) return
    task.classList.remove('is-working', 'is-complete', 'is-failed')
    if (status === 'Running') task.classList.add('is-working')
    if (status === 'Succeeded') task.classList.add('is-complete')
    if (status === 'Failed') task.classList.add('is-failed')
  }, [findElement])

  const setActorStatus = useCallback((actorId: string | undefined, status?: string) => {
    const actor = findElement(actorId)
    if (!actor) return
    actor.classList.remove('is-running', 'is-success', 'is-failed')
    if (status === 'Running') actor.classList.add('is-running')
    if (status === 'Succeeded') actor.classList.add('is-success')
    if (status === 'Failed') actor.classList.add('is-failed')
  }, [findElement])

  const applyEvent = useCallback((event: SimulationEvent) => {
    const actorPosition = event.actorId ? readPosition(event.actorId) : undefined
    const targetPosition = event.targetActorId ? readPosition(event.targetActorId) : undefined
    setFlowStatus(event)

    switch (event.type) {
      case 'actor.spawned': {
        showElement(event.actorId, true)
        if (event.actorId) {
          const destination = event.x !== undefined && event.y !== undefined
            ? { x: event.x, y: event.y }
            : readPosition(event.actorId)
          if (event.x !== undefined && event.y !== undefined) {
            setPosition(event.actorId, { x: destination.x, y: destination.y - 120 }, 0, .35)
          }
          animateMotion(event.actorId, destination, event.durationMs, { mode: 'spawn' })
          animateCharacterPose(event.actorId, 'arrive', Math.max(420, event.durationMs))
        }
        break
      }
      case 'actor.cloned': {
        animateCharacterPose(event.actorId, 'clone', Math.max(500, event.durationMs))
        if (event.targetActorId) {
          showElement(event.targetActorId, true)
          if (actorPosition) {
            const branchNumber = Number.parseInt(event.branchId?.replace(/\D/g, '') ?? '1', 10)
            const direction = branchNumber % 2 === 1 ? -1 : 1
            const row = Math.floor((branchNumber - 1) / 2)
            const distance = 195 + row * 105
            const destination = {
              x: actorPosition.x + direction * distance,
              y: actorPosition.y + row * 110,
            }
            setPosition(event.targetActorId, actorPosition, 0, .2)
            animateMotion(event.targetActorId, destination, event.durationMs, { mode: 'spawn' })
            animateCharacterPose(event.targetActorId, 'clone', Math.max(500, event.durationMs), direction)
            positionsRef.current.set(event.targetActorId, destination)
          }
        }
        break
      }
      case 'actor.moved': {
        if (event.x !== undefined && event.y !== undefined) {
          const destination = { x: event.x, y: event.y }
          const direction = !actorPosition || destination.x >= actorPosition.x ? 1 : -1
          if (actorPosition && !event.edgeId) drawMotionTrail(actorPosition, destination, event.durationMs, '#4f8ff7')
          animateMotion(event.actorId, destination, event.durationMs, { mode: 'walk', pathId: event.edgeId })
          animateCharacterPose(event.actorId, 'walk', event.durationMs, direction)
          if (event.taskId) animateMotion(event.taskId, { x: event.x + 64, y: event.y - 82 }, event.durationMs, { mode: 'walk' })
          pulseStation(event.stationId, event.durationMs + 350)
        }
        break
      }
      case 'actor.waiting': {
        if (event.x !== undefined && event.y !== undefined && actorPosition) {
          const destination = { x: event.x, y: event.y }
          if (Math.hypot(destination.x - actorPosition.x, destination.y - actorPosition.y) > 4) {
            animateMotion(event.actorId, destination, Math.min(600, event.durationMs), { mode: 'walk' })
          }
        }
        animateCharacterPose(event.actorId, event.stepType ? 'wait' : 'type', Math.max(500, event.durationMs))
        pulseStation(event.stationId, event.durationMs)
        animateDesk(event.stationId, event.durationMs)
        break
      }
      case 'actor.merged': {
        animateCharacterPose(event.actorId, 'merge', Math.max(420, event.durationMs))
        animateCharacterPose(event.targetActorId, 'merge', Math.max(420, event.durationMs))
        if (event.x !== undefined && event.y !== undefined) {
          animateMotion(event.actorId, { x: event.x, y: event.y }, event.durationMs, { mode: 'merge', hideAfter: true })
        } else if (targetPosition) {
          animateMotion(event.actorId, targetPosition, event.durationMs, { mode: 'merge', hideAfter: true })
        }
        break
      }
      case 'task.dropped': {
        if (event.x !== undefined && event.y !== undefined) {
          const destination = { x: event.x, y: event.y }
          if (event.taskId && event.taskId !== 'task-root') {
            setPosition(event.taskId, { x: destination.x, y: destination.y - 175 }, -12, .75)
          }
          showElement(event.taskId, true)
          animateMotion(event.taskId, destination, event.durationMs, { mode: 'drop' })
        }
        break
      }
      case 'task.picked_up':
        if (actorPosition) {
          const destination = { x: actorPosition.x + 68, y: actorPosition.y - 82 }
          drawMotionTrail(readPosition(event.taskId ?? ''), destination, event.durationMs, '#f4bc45')
          animateMotion(event.taskId, destination, event.durationMs, { mode: 'arc', arc: 55 })
          animateCharacterPose(event.actorId, 'pickup', Math.max(360, event.durationMs))
        }
        break
      case 'task.handed_off':
        if (targetPosition) {
          const direction = !actorPosition || targetPosition.x >= actorPosition.x ? 1 : -1
          const destination = { x: targetPosition.x + 68, y: targetPosition.y - 82 }
          drawMotionTrail(readPosition(event.taskId ?? ''), destination, event.durationMs, event.status === 'Failed' ? '#ef5b67' : '#38f8df')
          animateMotion(event.taskId, destination, event.durationMs, { mode: 'arc', arc: 115 })
          animateCharacterPose(event.actorId, 'handoff', Math.max(420, event.durationMs), direction)
          animateCharacterPose(event.targetActorId, 'pickup', Math.max(420, event.durationMs), -direction)
        }
        if (event.status === 'Failed') setTaskStatus(event.taskId, 'Failed')
        break
      case 'task.cloned':
        showElement(event.taskId, true)
        if (actorPosition && event.taskId) {
          setPosition(event.taskId, { x: actorPosition.x, y: actorPosition.y - 40 }, 0, .2)
          animateMotion(event.taskId, { x: actorPosition.x + 68, y: actorPosition.y - 82 }, event.durationMs, { mode: 'spawn' })
        }
        break
      case 'task.merged':
        if (targetPosition) animateMotion(event.taskId, { x: targetPosition.x + 68, y: targetPosition.y - 82 }, event.durationMs, { mode: 'merge', hideAfter: true })
        break
      case 'task.completed': {
        setTaskStatus(event.taskId, event.status)
        if (event.taskId) {
          const from = readPosition(event.taskId)
          if (event.status === 'Failed') {
            animateMotion(event.taskId, from, Math.max(500, event.durationMs), { mode: 'work' })
          } else {
            const direction = event.sequence % 2 === 0 ? 1 : -1
            animateMotion(
              event.taskId,
              { x: from.x + direction * 78, y: from.y - 115 },
              event.durationMs,
              { mode: 'complete', hideAfter: true },
            )
          }
        }
        break
      }
      case 'step.started':
        if (event.stationId || event.nodeId) {
          setActorStatus(event.actorId, 'Running')
          animateCharacterPose(event.actorId, actionForStep(event.stepType), event.durationMs)
          pulseStation(event.stationId, event.durationMs)
          animateDesk(event.stationId, event.durationMs)
        }
        break
      case 'step.completed':
        if (event.stationId || event.nodeId) {
          setActorStatus(event.actorId, event.status)
          updateParcelProgress(event.progressCurrent, event.progressTotal, event.status === 'Failed')
          animateCharacterPose(
            event.actorId,
            event.status === 'Failed' ? 'fail' : 'celebrate',
            event.status === 'Failed' ? 900 : 520,
          )
        }
        break
      case 'output.sent':
        if (event.x !== undefined && event.y !== undefined) {
          setTaskStatus(event.taskId, event.status)
          animateMotion(event.taskId, { x: event.x, y: event.y }, event.durationMs, { mode: 'sky', hideAfter: true })
          animateCharacterPose(event.actorId, event.status === 'Failed' ? 'fail' : 'deliver', Math.max(700, event.durationMs))
        }
        break
      case 'simulation.completed':
        setRunStatus(event.status === 'Failed' ? 'Failed' : 'Completed')
        setActorStatus(event.actorId, event.status)
        animateCharacterPose(event.actorId, event.status === 'Failed' ? 'fail' : 'celebrate', 1400)
        break
    }

    const statusText = svgHostRef.current?.querySelector<SVGTextElement>('#simulation-status')
    if (statusText && event.message) statusText.textContent = event.message
    const collapsedStep = (event.type === 'step.started' || event.type === 'step.completed')
      && !event.targetNodeId
      && !event.stationId
      && !event.nodeId
    const focusId = collapsedStep
      ? undefined
      : event.targetNodeId ?? event.stationId ?? event.nodeId ?? event.actorId
    if (focusId) requestAnimationFrame(() => focusElement(focusId))
    if (event.message) {
      setFeed(previous => [...previous.slice(-149), {
        key: `${prepared?.simulationId ?? 'run'}-${event.sequence}-${event.type}`,
        type: event.type,
        message: event.message!,
        status: event.status,
        stepId: event.stepId,
        workflowName: event.workflowName,
      }])
    }
  }, [animateCharacterPose, animateDesk, animateMotion, drawMotionTrail, focusElement, prepared?.simulationId, pulseStation, readPosition, setActorStatus, setFlowStatus, setPosition, setTaskStatus, showElement, updateParcelProgress])

  const handleEnvelope = useCallback(async (envelope: StreamEnvelope) => {
    if (envelope.prepared) {
      cancelMotions()
      positionsRef.current.clear()
      setPrepared(envelope.prepared)
      setSvg(envelope.prepared.svg)
      setFeed([])
      setRunStatus('Running')
      setAutoFollow(true)
      await nextFrame()
      await nextFrame()
      workflowAnimationsRef.current?.attach()
      const viewport = svgHostRef.current
      if (viewport) {
        setZoom(Math.max(.28, Math.min(1, (viewport.clientWidth - 24) / envelope.prepared.canvasWidth)))
        viewport.scrollTo({ left: 0, top: 0 })
      }
      return
    }
    if (envelope.event) {
      workflowAnimationsRef.current?.applyEvent(envelope.event)
      if (envelope.event.type === 'simulation.completed')
        setRunStatus(envelope.event.status === 'Failed' ? 'Failed' : 'Completed')
      if (envelope.event.message) {
        setFeed(previous => [...previous.slice(-149), {
          key: `${prepared?.simulationId ?? 'run'}-${envelope.event!.sequence}-${envelope.event!.type}`,
          type: envelope.event!.type,
          message: envelope.event!.message!,
          status: envelope.event!.status,
          stepId: envelope.event!.stepId,
          workflowName: envelope.event!.workflowName,
        }])
      }
    }
  }, [cancelMotions, prepared?.simulationId])

  const run = useCallback(async () => {
    abortRef.current?.abort()
    const controller = new AbortController()
    abortRef.current = controller
    setRunError(null)
    setRunning(true)
    setRunStatus('Starting')
    try {
      await streamSimulation(buildRequest(true), handleEnvelope, controller.signal)
    } catch (error) {
      if (!controller.signal.aborted) {
        setRunError(error instanceof Error ? error.message : String(error))
        setRunStatus('Error')
      }
    } finally {
      if (abortRef.current === controller) {
        setRunning(false)
        abortRef.current = null
      }
    }
  }, [buildRequest, handleEnvelope])

  const stop = useCallback(() => {
    abortRef.current?.abort()
    abortRef.current = null
    cancelMotions()
    workflowAnimationsRef.current?.dispose()
    setRunning(false)
    setRunStatus('Stopped')
  }, [cancelMotions])

  const downloadSvg = useCallback(() => {
    if (!svg) return
    const url = URL.createObjectURL(new Blob([svg], { type: 'image/svg+xml' }))
    const link = document.createElement('a')
    link.href = url
    link.download = `gnougo-team-${prepared?.seed ?? seed}.svg`
    link.click()
    URL.revokeObjectURL(url)
  }, [prepared?.seed, seed, svg])

  const valid = validation?.valid === true && !inputError
  const diagnostics = useMemo(() => validation?.diagnostics ?? [], [validation])

  useEffect(() => {
    if (autoRunRef.current || !valid || running || prepared) return
    autoRunRef.current = true
    void run()
  }, [prepared, run, running, valid])

  return (
    <div className="app-shell">
      <header className="topbar">
        <div>
          <p className="eyebrow">Autonomous visual simulator</p>
          <h1>GnOuGo Team Animation</h1>
        </div>
        <div className={`run-pill run-pill--${runStatus.toLowerCase()}`}>{runStatus}</div>
      </header>

      <main className="workspace">
        <aside className="control-panel">
          <section className="panel-section intro-card">
            <h2>Preview workflow</h2>
            <p>Only long-running LLM and MCP work becomes a clean isometric laptop desk. GnOuGos follow curved, translucent asphalt routes between those desks.</p>
            <p>Parallel, foreach, decision, and handoff signs appear only when they lead to visible long-running work. The demo intentionally alternates faster MCP tasks and longer LLM focus sessions.</p>
            <p>Slow breathing, blinking, mouth movement, independent ear twitches, and rare yawns keep every visible GnOuGo alive between actions and during long tasks.</p>
            <p>This parser is intentionally independent. It resembles GnOuGo.Flow YAML but does not validate or execute Flow.Core.</p>
          </section>

          <section className="panel-section editor-section">
            <div className="section-heading"><label htmlFor="workflow">Workflow-shaped YAML</label><span>{workflow.length.toLocaleString()} chars</span></div>
            <textarea id="workflow" className="code-editor workflow-editor" value={workflow} onChange={event => setWorkflow(event.target.value)} spellCheck={false} />
          </section>

          <section className="panel-section">
            <div className="section-heading"><label htmlFor="inputs">Preview inputs</label><span>JSON object</span></div>
            <textarea id="inputs" className="code-editor inputs-editor" value={inputs} onChange={event => setInputs(event.target.value)} spellCheck={false} />
            {inputError && <p className="inline-error">{inputError}</p>}
          </section>

          <section className="panel-section options-grid">
            <label>Environment<select value={scene} onChange={event => setScene(event.target.value as SceneKind)}>{scenes.map(item => <option key={item}>{item}</option>)}</select></label>
            <label>Seed<div className="seed-row"><input type="number" value={seed} onChange={event => setSeed(event.target.value)} /><button type="button" onClick={() => setSeed(String(crypto.getRandomValues(new Uint32Array(1))[0] || 1))} title="Randomize seed">↻</button></div></label>
            <label>Speed<select value={speed} onChange={event => setSpeed(Number(event.target.value))}>{speeds.map(item => <option key={item} value={item}>{item}×</option>)}</select></label>
            <label>Inject failure<select value={failAt} onChange={event => setFailAt(event.target.value)}><option value="">Success</option>{validation?.failureTargets.map(target => <option key={failureValue(target)} value={failureValue(target)}>{target.label}</option>)}</select></label>
          </section>

          <section className="panel-section validation-card">
            <div className="section-heading"><h2>Preview validation</h2><span>{validationBusy ? 'Checking…' : valid ? 'Valid' : 'Needs attention'}</span></div>
            <p className="validation-disclaimer">Not Flow.Core validation.</p>
            {diagnostics.length === 0 && valid && <p className="valid-message">✓ Structure is ready to simulate.</p>}
            {diagnostics.map((diagnostic, index) => (
              <div className={`diagnostic diagnostic--${diagnostic.severity.toLowerCase()}`} key={`${diagnostic.code}-${index}`}>
                <strong>{diagnostic.code}</strong><span>{diagnostic.message}</span>
              </div>
            ))}
          </section>

          <div className="action-row">
            <button className="primary-button" type="button" disabled={!valid || running} onClick={run}>{running ? 'Simulating…' : 'Run simulation'}</button>
            <button className="secondary-button" type="button" disabled={!running} onClick={stop}>Stop</button>
          </div>
          {runError && <p className="run-error">{runError}</p>}
        </aside>

        <section className="stage-panel">
          <div className="stage-toolbar">
            <div>
              <strong>{prepared ? `${prepared.scene} · seed ${prepared.seed}` : 'Waiting for a simulation'}</strong>
              <span>{prepared ? `${prepared.laneCount} workflow lanes · ${prepared.nodeCount} nodes · ${prepared.actorCount} actors · ${(prepared.durationMs / 1000).toFixed(1)}s` : 'Choose options and run the preview.'}</span>
            </div>
            <div className="toolbar-actions">
              <button type="button" disabled={!svg} onClick={() => setZoom(value => Math.max(.28, value - .1))} title="Zoom out">−</button>
              <button type="button" disabled={!svg} onClick={() => setZoom(value => Math.min(1.6, value + .1))} title="Zoom in">+</button>
              <button type="button" disabled={!svg} onClick={fitCanvas}>Fit</button>
              <button type="button" disabled={!svg} onClick={() => focusElement(lastFocusRef.current ?? 'actor-master', true)}>Center</button>
              <button type="button" disabled={!svg} className={autoFollow ? 'is-selected' : ''} onClick={() => setAutoFollow(value => !value)}>Follow</button>
              <button type="button" disabled={!svg || running} onClick={run}>Replay</button>
              <button type="button" disabled={!svg} onClick={downloadSvg}>Download SVG</button>
            </div>
          </div>

          <div
            className={`stage ${dragRef.current ? 'is-dragging' : ''}`}
            ref={svgHostRef}
            onPointerDown={handleStagePointerDown}
            onPointerMove={handleStagePointerMove}
            onPointerUp={handleStagePointerUp}
            onPointerCancel={handleStagePointerUp}
            onWheel={handleStageWheel}
          >
            {svg
              ? <MotionSvgScene
                  svg={svg}
                  width={prepared?.canvasWidth ?? 1600}
                  height={prepared?.canvasHeight ?? 900}
                  zoom={zoom}
                />
              : <div className="empty-stage"><span>🐻</span><h2>The GnOuGo team is waiting.</h2><p>The input parcel will arrive from the sky when the simulation starts.</p></div>}
          </div>

          <div className="observability-grid">
            <section className="event-feed">
              <div className="section-heading"><h2>Synthetic observability</h2><span>{feed.length} events</span></div>
              <div className="feed-scroll" aria-live="polite">
                {feed.length === 0 && <p className="muted">Lifecycle events will appear here.</p>}
                {feed.slice().reverse().map(item => (
                  <div className={`feed-item ${item.status ? `feed-item--${item.status.toLowerCase()}` : ''}`} key={item.key}>
                    <span className="feed-dot" />
                    <div><strong>{item.stepId ?? item.type}</strong><p>{item.message}</p>{item.workflowName && <small>{item.workflowName}</small>}</div>
                  </div>
                ))}
              </div>
            </section>
            <section className="legend-card">
              <h2>Visual language</h2>
              <ul><li><span className="legend-dot running" />Active asphalt route</li><li><span className="legend-dot success" />Completed LLM/MCP desk</li><li><span className="legend-dot failed" />Failed route</li><li><span className="legend-dot matrix" />Useful transition sign</li><li><span className="legend-dot task" />Project parcel</li><li><span className="legend-dot handoff" />Workflow handoff</li></ul>
              {prepared?.warnings.length ? <><h3>Preview decisions</h3>{prepared.warnings.map((warning, index) => <p key={`${warning.code}-${index}`}>{warning.message}</p>)}</> : null}
            </section>
          </div>
        </section>
      </main>
    </div>
  )
}
