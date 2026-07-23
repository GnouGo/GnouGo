import { memo, useCallback, useEffect, useMemo, useRef, useState, type PointerEvent as ReactPointerEvent, type WheelEvent as ReactWheelEvent } from 'react'
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
interface RigTransform { angle: number; x: number; y: number; scaleX: number; scaleY: number }
interface AmbientLife {
  breath: number
  look: number
  blink: number
  leftEar: number
  rightEar: number
  mouth: number
  yawn: number
}
type MotionMode = 'walk' | 'arc' | 'drop' | 'work' | 'spawn' | 'merge' | 'complete' | 'sky'
type CharacterAction = 'walk' | 'arrive' | 'pickup' | 'handoff' | 'type' | 'wait' | 'deliver' | 'think' | 'communicate' | 'write' | 'plan' | 'ask' | 'build' | 'clone' | 'merge' | 'celebrate' | 'fail'

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

function periodicPulse(seconds: number, period: number, phaseOffset: number, halfWidth: number): number {
  const phase = ((seconds / period + phaseOffset) % 1 + 1) % 1
  const distance = Math.min(Math.abs(phase - .5), 1 - Math.abs(phase - .5))
  if (distance >= halfWidth) return 0
  const value = 1 - distance / halfWidth
  return value * value * (3 - 2 * value)
}

function ambientLifeAt(now: number, seed: number): AmbientLife {
  const seconds = now / 1000
  const stableSeed = Math.abs(seed || 1)
  const breathPeriod = 6.8 + stableSeed % 6 * .22
  const lookPeriod = 12.5 + stableSeed % 8 * .37
  const phase = (stableSeed % 97) / 97
  return {
    breath: Math.sin((seconds / breathPeriod + phase) * Math.PI * 2),
    look: Math.sin((seconds / lookPeriod + phase * .7) * Math.PI * 2),
    blink: periodicPulse(seconds, 6.1 + stableSeed % 7 * .43, phase * .83, .018),
    leftEar: periodicPulse(seconds, 9.5 + stableSeed % 5 * .71, phase * .61, .055),
    rightEar: periodicPulse(seconds, 11.3 + stableSeed % 7 * .63, phase * .37 + .29, .05),
    mouth: Math.sin((seconds / (9.2 + stableSeed % 5 * .41) + phase) * Math.PI * 2),
    yawn: periodicPulse(seconds, 48 + stableSeed % 19, phase * .47 + .13, .048),
  }
}

function actionForStep(stepType?: string): CharacterAction {
  const normalized = stepType?.toLowerCase() ?? ''
  if (normalized.startsWith('workflow.')) return 'handoff'
  return 'type'
}

function rigPart(actor: SVGGraphicsElement, name: string): SVGGraphicsElement | null {
  return actor.querySelector<SVGGraphicsElement>(`[data-part="${name}"]`)
}

function applyRigTransform(
  part: SVGGraphicsElement | null,
  angle = 0,
  x = 0,
  y = 0,
  scaleX = 1,
  scaleY = 1,
) {
  if (!part) return
  const pivotX = Number(part.getAttribute('data-pivot-x') ?? 0)
  const pivotY = Number(part.getAttribute('data-pivot-y') ?? 0)
  part.setAttribute(
    'transform',
    `translate(${x} ${y}) rotate(${angle} ${pivotX} ${pivotY}) translate(${pivotX} ${pivotY}) scale(${scaleX} ${scaleY}) translate(${-pivotX} ${-pivotY})`,
  )
}

function readRigTransform(part: SVGGraphicsElement): RigTransform {
  const transform = part.getAttribute('transform') ?? ''
  const translation = /translate\(([-0-9.]+) ([-0-9.]+)\)/.exec(transform)
  const rotation = /rotate\(([-0-9.]+)/.exec(transform)
  const scale = /scale\(([-0-9.]+) ([-0-9.]+)\)/.exec(transform)
  return {
    angle: Number(rotation?.[1] ?? 0),
    x: Number(translation?.[1] ?? 0),
    y: Number(translation?.[2] ?? 0),
    scaleX: Number(scale?.[1] ?? 1),
    scaleY: Number(scale?.[2] ?? 1),
  }
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
  const positionsRef = useRef(new Map<string, Position>())
  const animationsRef = useRef(new Map<string, number>())
  const poseAnimationsRef = useRef(new Map<string, number>())
  const deskAnimationsRef = useRef<Animation[]>([])
  const ambientFrameRef = useRef<number | null>(null)
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
    poseAnimationsRef.current.forEach(frame => cancelAnimationFrame(frame))
    poseAnimationsRef.current.clear()
    deskAnimationsRef.current.forEach(animation => animation.cancel())
    deskAnimationsRef.current = []
    svgHostRef.current?.querySelectorAll<SVGGraphicsElement>('.gnougo-actor').forEach(actor => {
      actor.querySelectorAll<SVGGraphicsElement>('[data-part]').forEach(part => part.removeAttribute('transform'))
      const effects = rigPart(actor, 'action-fx')
      if (effects) effects.setAttribute('opacity', '0')
      actor.setAttribute('data-pose', 'idle')
    })
  }, [])

  const resetCharacterPose = useCallback((actorId?: string) => {
    const actor = findElement(actorId)
    if (!actor) return
    const previousFrame = actorId ? poseAnimationsRef.current.get(actorId) : undefined
    if (previousFrame !== undefined) cancelAnimationFrame(previousFrame)
    if (actorId) poseAnimationsRef.current.delete(actorId)
    actor.querySelectorAll<SVGGraphicsElement>('[data-part]').forEach(part => part.removeAttribute('transform'))
    const effects = rigPart(actor, 'action-fx')
    if (effects) effects.setAttribute('opacity', '0')
    actor.setAttribute('data-pose', 'idle')
  }, [findElement])

  const animateCharacterPose = useCallback((
    actorId: string | undefined,
    action: CharacterAction,
    duration: number,
    direction = 1,
  ) => {
    if (!actorId) return
    const actor = findElement(actorId)
    if (!actor?.querySelector('[data-animation-rig="true"]')) return
    const previousFrame = poseAnimationsRef.current.get(actorId)
    if (previousFrame !== undefined) cancelAnimationFrame(previousFrame)

    const reduced = window.matchMedia('(prefers-reduced-motion: reduce)').matches
    // Character gestures deliberately outlive very short event timings. A
    // following event can still replace the pose immediately, but no action
    // tries to squeeze several movements into a few frantic frames.
    const actualDuration = reduced ? 1 : Math.max(360, duration * 1.15)
    const generation = motionGenerationRef.current
    const startedAt = performance.now()
    actor.setAttribute('data-pose', action)

    const leftArm = rigPart(actor, 'arm-left')
    const rightArm = rigPart(actor, 'arm-right')
    const leftEar = rigPart(actor, 'ear-left')
    const rightEar = rigPart(actor, 'ear-right')
    const leftLeg = rigPart(actor, 'leg-left')
    const rightLeg = rigPart(actor, 'leg-right')
    const body = rigPart(actor, 'body')
    const head = rigPart(actor, 'head')
    const leftEye = rigPart(actor, 'eye-left')
    const rightEye = rigPart(actor, 'eye-right')
    const leftPupil = rigPart(actor, 'pupil-left')
    const rightPupil = rigPart(actor, 'pupil-right')
    const leftBrow = rigPart(actor, 'brow-left')
    const rightBrow = rigPart(actor, 'brow-right')
    const leftCheek = rigPart(actor, 'cheek-left')
    const rightCheek = rigPart(actor, 'cheek-right')
    const mouth = rigPart(actor, 'mouth')
    const bowTie = rigPart(actor, 'bow-tie')
    const effects = rigPart(actor, 'action-fx')
    const visualSeed = Number(actor.getAttribute('data-visual-seed') ?? 1)
    const initialTransforms = new Map<SVGGraphicsElement, RigTransform>()
    actor.querySelectorAll<SVGGraphicsElement>('[data-part]').forEach(part => {
      initialTransforms.set(part, readRigTransform(part))
    })
    const poseTargets = new Map<SVGGraphicsElement, RigTransform>()
    let poseBlend = 0
    const setRigTransform = (
      part: SVGGraphicsElement | null,
      angle = 0,
      x = 0,
      y = 0,
      scaleX = 1,
      scaleY = 1,
    ) => {
      if (!part) return
      poseTargets.set(part, { angle, x, y, scaleX, scaleY })
    }
    const addRigTransform = (
      part: SVGGraphicsElement | null,
      angle = 0,
      x = 0,
      y = 0,
      scaleX = 0,
      scaleY = 0,
    ) => {
      if (!part) return
      const target = poseTargets.get(part) ?? { angle: 0, x: 0, y: 0, scaleX: 1, scaleY: 1 }
      poseTargets.set(part, {
        angle: target.angle + angle,
        x: target.x + x,
        y: target.y + y,
        scaleX: target.scaleX * (1 + scaleX),
        scaleY: target.scaleY * (1 + scaleY),
      })
    }

    const render = (now: number) => {
      if (generation !== motionGenerationRef.current || !actor.isConnected) return
      const progress = Math.max(0, Math.min(1, (now - startedAt) / actualDuration))
      const gesture = Math.sin(progress * Math.PI)
      const elapsedSeconds = (now - startedAt) / 1000
      poseBlend = Math.min(1, elapsedSeconds / .22)
      const wave = Math.sin(elapsedSeconds * Math.PI * 1.9) * gesture
      const fastWave = Math.sin(elapsedSeconds * Math.PI * 2.2) * gesture
      const bounce = Math.abs(wave)
      const life = ambientLifeAt(now, visualSeed)
      const actionBlink = actualDuration >= 700
        ? Math.max(0, 1 - Math.abs(progress - .62) / .035)
        : 0
      const ambientStrength = action === 'type' || action === 'wait' || action === 'think' ? .72 : .34
      const eyeClose = Math.max(actionBlink, life.blink * ambientStrength, life.yawn * .52)
      const eyeScale = 1 - eyeClose * .9
      let pupilX = direction * 1.4
      let pupilY = 0
      let effectOpacity = 0

      setRigTransform(leftArm)
      setRigTransform(rightArm)
      setRigTransform(leftEar, wave * 2)
      setRigTransform(rightEar, -wave * 2)
      setRigTransform(leftLeg)
      setRigTransform(rightLeg)
      setRigTransform(body)
      setRigTransform(head)
      setRigTransform(leftBrow, -2)
      setRigTransform(rightBrow, 2)
      setRigTransform(leftCheek)
      setRigTransform(rightCheek)
      setRigTransform(mouth, 0, 0, -1, 1.04, 1.08)
      setRigTransform(bowTie)

      switch (action) {
        case 'walk':
          setRigTransform(leftLeg, wave * 29)
          setRigTransform(rightLeg, -wave * 29)
          setRigTransform(leftArm, -wave * 17)
          setRigTransform(rightArm, wave * 17)
          setRigTransform(body, wave * .8, 0, -bounce * .8)
          setRigTransform(head, -wave * .9, direction * .8, -bounce * .8)
          setRigTransform(leftEar, 4 + wave * 4.5)
          setRigTransform(rightEar, -4 - wave * 4.5)
          setRigTransform(leftCheek, 0, 0, 0, 1 + bounce * .04, 1 + bounce * .04)
          setRigTransform(rightCheek, 0, 0, 0, 1 + bounce * .04, 1 + bounce * .04)
          setRigTransform(mouth, wave * 1.2, 0, -1, 1.05, 1.12)
          break
        case 'arrive':
          setRigTransform(leftArm, 18 * gesture)
          setRigTransform(rightArm, -68 * gesture + fastWave * 5)
          setRigTransform(head, -direction * 4 * gesture, 0, -bounce)
          setRigTransform(leftEar, 10 * gesture + fastWave * 2)
          setRigTransform(rightEar, -10 * gesture - fastWave * 2)
          setRigTransform(leftBrow, -8 * gesture)
          setRigTransform(rightBrow, 8 * gesture)
          setRigTransform(leftCheek, 0, 0, 0, 1 + gesture * .12, 1 + gesture * .12)
          setRigTransform(rightCheek, 0, 0, 0, 1 + gesture * .12, 1 + gesture * .12)
          setRigTransform(mouth, 0, 0, -gesture * 2, 1 + gesture * .14, 1 + gesture * .22)
          effectOpacity = gesture * .65
          break
        case 'pickup':
          setRigTransform(leftArm, -41 * gesture)
          setRigTransform(rightArm, 41 * gesture)
          setRigTransform(leftLeg, 10 * gesture)
          setRigTransform(rightLeg, -10 * gesture)
          setRigTransform(body, 0, 0, 10 * gesture, 1, 1 - .06 * gesture)
          setRigTransform(head, direction * 2.5 * gesture, 0, 9 * gesture)
          setRigTransform(leftEar, -7 * gesture)
          setRigTransform(rightEar, 7 * gesture)
          pupilY = 4 * gesture
          break
        case 'handoff':
          setRigTransform(leftArm, -43 * gesture + fastWave * 1.5)
          setRigTransform(rightArm, 43 * gesture - fastWave * 1.5)
          setRigTransform(body, direction * 3 * gesture, direction * 3 * gesture)
          setRigTransform(head, direction * 5 * gesture, direction * 2 * gesture)
          setRigTransform(leftEar, direction * 10 * gesture)
          setRigTransform(rightEar, direction * 5 * gesture)
          pupilX = direction * 3
          effectOpacity = gesture * .55
          break
        case 'type':
          setRigTransform(leftArm, -34 * gesture + fastWave * 3.5)
          setRigTransform(rightArm, 34 * gesture - fastWave * 3.5)
          setRigTransform(leftLeg, wave * 1.5)
          setRigTransform(rightLeg, -wave * 1.5)
          setRigTransform(body, wave * .6, 0, bounce * .7)
          setRigTransform(head, wave, 0, 3.5 * gesture + bounce * .5)
          setRigTransform(leftEar, 3 + fastWave * 2.5)
          setRigTransform(rightEar, -3 - fastWave * 2.5)
          setRigTransform(leftBrow, -2 + fastWave * .7)
          setRigTransform(rightBrow, 2 - fastWave * .7)
          setRigTransform(leftCheek, 0, 0, 0, 1 + bounce * .06, 1 + bounce * .06)
          setRigTransform(rightCheek, 0, 0, 0, 1 + bounce * .06, 1 + bounce * .06)
          setRigTransform(mouth, fastWave * 1.2, 0, -1, 1.06, 1.13)
          pupilX = fastWave * .8
          pupilY = 3 * gesture
          effectOpacity = .12 + bounce * .22
          break
        case 'wait':
          setRigTransform(leftArm, 12 * gesture + wave * 1.5)
          setRigTransform(rightArm, -12 * gesture - wave * 1.5)
          setRigTransform(body, wave * .6, 0, -bounce * .5)
          setRigTransform(head, direction * (2 + wave), 0, -bounce * .5)
          setRigTransform(leftEar, wave * 4)
          setRigTransform(rightEar, -wave * 4)
          pupilX = direction * (1.3 + wave)
          break
        case 'deliver':
          setRigTransform(leftArm, 88 * gesture + fastWave * 2)
          setRigTransform(rightArm, -88 * gesture - fastWave * 2)
          setRigTransform(leftLeg, fastWave * 2)
          setRigTransform(rightLeg, -fastWave * 2)
          setRigTransform(body, 0, 0, -gesture * 8)
          setRigTransform(head, -fastWave, 0, -gesture * 4)
          setRigTransform(leftEar, 10 * gesture + fastWave * 2)
          setRigTransform(rightEar, -10 * gesture - fastWave * 2)
          setRigTransform(leftBrow, -10 * gesture)
          setRigTransform(rightBrow, 10 * gesture)
          setRigTransform(leftCheek, 0, 0, 0, 1 + gesture * .16, 1 + gesture * .16)
          setRigTransform(rightCheek, 0, 0, 0, 1 + gesture * .16, 1 + gesture * .16)
          setRigTransform(mouth, 0, 0, -gesture * 3, 1 + gesture * .18, 1 + gesture * .3)
          pupilY = -2.5 * gesture
          effectOpacity = .4 + .6 * bounce
          break
        case 'think':
          setRigTransform(leftArm, -22 * gesture)
          setRigTransform(rightArm, 82 * gesture + fastWave * 1.5)
          setRigTransform(head, -5 * gesture + wave * .7, 0, -2 * gesture)
          pupilX = 2 * gesture
          pupilY = -2.5 * gesture
          effectOpacity = .25 + .75 * bounce
          break
        case 'communicate':
          setRigTransform(leftArm, -26 * gesture + fastWave * 3)
          setRigTransform(rightArm, -94 * gesture + fastWave * 3)
          setRigTransform(head, wave * 1.5)
          pupilX = wave * 2
          effectOpacity = .35 + .65 * bounce
          break
        case 'write':
          setRigTransform(leftArm, -34 * gesture + fastWave * 2.5)
          setRigTransform(rightArm, 34 * gesture - fastWave * 2.5)
          setRigTransform(head, wave, 0, 4 * gesture + bounce * .5)
          pupilX = fastWave
          pupilY = 3 * gesture
          setRigTransform(body, 0, 0, bounce * .7)
          break
        case 'plan':
          setRigTransform(leftArm, 75 * gesture + fastWave * 2.5)
          setRigTransform(rightArm, -24 * gesture)
          setRigTransform(head, -direction * 4 * gesture)
          pupilX = direction * 3 * gesture
          pupilY = -2 * gesture
          effectOpacity = .2 + .7 * bounce
          break
        case 'ask':
          setRigTransform(leftArm, -22 * gesture)
          setRigTransform(rightArm, -92 * gesture + fastWave * 7)
          setRigTransform(head, direction * (3 + wave) * gesture)
          pupilX = direction * 3
          effectOpacity = gesture * .7
          break
        case 'build':
          setRigTransform(leftArm, -38 * gesture)
          setRigTransform(rightArm, -58 * gesture + fastWave * 13)
          setRigTransform(head, -fastWave, 0, bounce)
          setRigTransform(body, fastWave * .7)
          effectOpacity = bounce * .45
          break
        case 'clone':
          setRigTransform(leftArm, 70 * gesture)
          setRigTransform(rightArm, -70 * gesture)
          setRigTransform(leftLeg, -14 * gesture)
          setRigTransform(rightLeg, 14 * gesture)
          setRigTransform(head, fastWave * 2, 0, -gesture * 7)
          setRigTransform(leftEar, 15 * gesture + fastWave * 2.5)
          setRigTransform(rightEar, -15 * gesture - fastWave * 2.5)
          setRigTransform(body, 0, 0, -gesture * 5, 1 + gesture * .08, 1 + gesture * .08)
          effectOpacity = .35 + .65 * bounce
          break
        case 'merge':
          setRigTransform(leftArm, -44 * gesture)
          setRigTransform(rightArm, 44 * gesture)
          setRigTransform(head, -fastWave * 1.3)
          effectOpacity = gesture
          break
        case 'celebrate':
          setRigTransform(leftArm, 90 * gesture + fastWave * 3)
          setRigTransform(rightArm, -90 * gesture - fastWave * 3)
          setRigTransform(leftLeg, fastWave * 3)
          setRigTransform(rightLeg, -fastWave * 3)
          setRigTransform(body, fastWave * .8, 0, -bounce * 5)
          setRigTransform(head, -fastWave * 1.5, 0, -bounce * 3)
          setRigTransform(leftEar, 13 * gesture + fastWave * 4)
          setRigTransform(rightEar, -13 * gesture - fastWave * 4)
          setRigTransform(leftBrow, -12 * gesture)
          setRigTransform(rightBrow, 12 * gesture)
          setRigTransform(leftCheek, 0, 0, 0, 1 + gesture * .2, 1 + gesture * .2)
          setRigTransform(rightCheek, 0, 0, 0, 1 + gesture * .2, 1 + gesture * .2)
          setRigTransform(mouth, 0, 0, -gesture * 2, 1 + gesture * .18, 1 + gesture * .28)
          setRigTransform(bowTie, fastWave * 10)
          pupilY = -2 * gesture
          effectOpacity = .45 + .55 * bounce
          break
        case 'fail':
          {
            const slump = Math.min(1, progress * 3)
            setRigTransform(leftArm, -8 * slump)
            setRigTransform(rightArm, 8 * slump)
            setRigTransform(leftLeg, 6 * slump)
            setRigTransform(rightLeg, -6 * slump)
            setRigTransform(body, 0, 0, 7 * slump, 1, 1 - .05 * slump)
            setRigTransform(head, 12 * slump, 0, 11 * slump)
            setRigTransform(leftEar, -16 * slump)
            setRigTransform(rightEar, 16 * slump)
            setRigTransform(leftBrow, 10 * slump)
            setRigTransform(rightBrow, -10 * slump)
            setRigTransform(leftCheek, 0, 0, 1 * slump, 1 - slump * .08, 1 - slump * .08)
            setRigTransform(rightCheek, 0, 0, 1 * slump, 1 - slump * .08, 1 - slump * .08)
            setRigTransform(mouth, 0, 0, 3 * slump, 1, .7)
          }
          pupilX = 0
          pupilY = 5 * Math.min(1, progress * 3)
          break
      }

      setRigTransform(leftEye, 0, 0, 0, 1, eyeScale)
      setRigTransform(rightEye, 0, 0, 0, 1, eyeScale)
      setRigTransform(leftPupil, 0, pupilX + life.look * .55 * ambientStrength, pupilY + life.breath * .12)
      setRigTransform(rightPupil, 0, pupilX + life.look * .55 * ambientStrength, pupilY + life.breath * .12)

      addRigTransform(body, life.breath * .12 * ambientStrength, 0, -Math.abs(life.breath) * .2, 0, life.breath * .002)
      addRigTransform(head, life.look * .32 * ambientStrength - life.yawn * 2.2, 0, -Math.abs(life.breath) * .18)
      addRigTransform(leftEar, life.leftEar * 3.8 * ambientStrength + life.breath * .35)
      addRigTransform(rightEar, -life.rightEar * 3.4 * ambientStrength - life.breath * .28)
      addRigTransform(
        mouth,
        life.mouth * .35 * ambientStrength,
        0,
        life.yawn * 1.8,
        life.mouth * .012 * ambientStrength,
        life.yawn * 1.15 + Math.abs(life.mouth) * .018 * ambientStrength,
      )
      if ((action === 'wait' || action === 'think') && life.yawn > 0) {
        addRigTransform(rightArm, -36 * life.yawn)
        addRigTransform(leftArm, 5 * life.yawn)
      }

      const blend = poseBlend * poseBlend * (3 - 2 * poseBlend)
      poseTargets.forEach((target, part) => {
        const initial = initialTransforms.get(part) ?? { angle: 0, x: 0, y: 0, scaleX: 1, scaleY: 1 }
        applyRigTransform(
          part,
          initial.angle + (target.angle - initial.angle) * blend,
          initial.x + (target.x - initial.x) * blend,
          initial.y + (target.y - initial.y) * blend,
          initial.scaleX + (target.scaleX - initial.scaleX) * blend,
          initial.scaleY + (target.scaleY - initial.scaleY) * blend,
        )
      })
      if (effects) {
        effects.setAttribute('opacity', String(effectOpacity))
        effects.setAttribute('transform', `translate(0 ${-bounce * 8}) scale(${1 + bounce * .08})`)
      }

      if (progress < 1) {
        const frame = requestAnimationFrame(render)
        poseAnimationsRef.current.set(actorId, frame)
        return
      }

      poseAnimationsRef.current.delete(actorId)
      if (action !== 'fail') resetCharacterPose(actorId)
      else actor.setAttribute('data-pose', 'fail')
    }

    const frame = requestAnimationFrame(render)
    poseAnimationsRef.current.set(actorId, frame)
  }, [findElement, resetCharacterPose])

  useEffect(() => {
    const renderAmbient = (now: number) => {
      const reduced = window.matchMedia('(prefers-reduced-motion: reduce)').matches
      if (!reduced) {
        svgHostRef.current?.querySelectorAll<SVGGraphicsElement>('.gnougo-actor[data-visible="true"]').forEach(actor => {
          const pose = actor.getAttribute('data-pose')
          if (pose !== 'idle' && pose !== 'fail') return
          const seed = Number(actor.getAttribute('data-visual-seed') ?? 1)
          const life = ambientLifeAt(now, seed)
          if (pose === 'fail') {
            applyRigTransform(rigPart(actor, 'body'), life.breath * .08, 0, 7 - Math.abs(life.breath) * .16, 1, .95 + life.breath * .002)
            applyRigTransform(rigPart(actor, 'head'), 12 + life.look * .24, 0, 11 - Math.abs(life.breath) * .12)
            applyRigTransform(rigPart(actor, 'arm-left'), -8 + life.breath * .18)
            applyRigTransform(rigPart(actor, 'arm-right'), 8 - life.breath * .18)
            applyRigTransform(rigPart(actor, 'ear-left'), -16 + life.leftEar * 2)
            applyRigTransform(rigPart(actor, 'ear-right'), 16 - life.rightEar * 2)
            applyRigTransform(rigPart(actor, 'eye-left'), 0, 0, 0, 1, 1 - life.blink * .9)
            applyRigTransform(rigPart(actor, 'eye-right'), 0, 0, 0, 1, 1 - life.blink * .9)
            applyRigTransform(rigPart(actor, 'pupil-left'), 0, life.look * .25, 5)
            applyRigTransform(rigPart(actor, 'pupil-right'), 0, life.look * .25, 5)
            applyRigTransform(rigPart(actor, 'mouth'), life.mouth * .16, 0, 3, 1, .7 + Math.abs(life.mouth) * .012)
            return
          }
          const eyeClose = Math.max(life.blink, life.yawn * .58)

          applyRigTransform(rigPart(actor, 'body'), life.breath * .22, 0, -Math.abs(life.breath) * .45, 1, 1 + life.breath * .004)
          applyRigTransform(rigPart(actor, 'head'), life.look * .62 - life.yawn * 3.8, 0, -Math.abs(life.breath) * .32 - life.yawn * 1.2)
          applyRigTransform(rigPart(actor, 'arm-left'), 5 * life.yawn)
          applyRigTransform(rigPart(actor, 'arm-right'), -58 * life.yawn)
          applyRigTransform(rigPart(actor, 'ear-left'), life.leftEar * 7 + life.breath * .55 - life.yawn * 2)
          applyRigTransform(rigPart(actor, 'ear-right'), -life.rightEar * 6 - life.breath * .45 + life.yawn * 2)
          applyRigTransform(rigPart(actor, 'eye-left'), 0, 0, 0, 1, 1 - eyeClose * .9)
          applyRigTransform(rigPart(actor, 'eye-right'), 0, 0, 0, 1, 1 - eyeClose * .9)
          applyRigTransform(rigPart(actor, 'pupil-left'), 0, life.look * .9, life.breath * .16)
          applyRigTransform(rigPart(actor, 'pupil-right'), 0, life.look * .9, life.breath * .16)
          applyRigTransform(
            rigPart(actor, 'mouth'),
            life.mouth * .42,
            0,
            -1 + life.yawn * 2.4,
            1.04 + life.mouth * .012,
            1.08 + life.yawn * 1.42 + Math.abs(life.mouth) * .02,
          )
        })
      }
      ambientFrameRef.current = requestAnimationFrame(renderAmbient)
    }
    ambientFrameRef.current = requestAnimationFrame(renderAmbient)
    return () => {
      if (ambientFrameRef.current !== null) cancelAnimationFrame(ambientFrameRef.current)
      ambientFrameRef.current = null
    }
  }, [svg])

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
      const viewport = svgHostRef.current
      if (viewport) {
        setZoom(Math.max(.28, Math.min(1, (viewport.clientWidth - 24) / envelope.prepared.canvasWidth)))
        viewport.scrollTo({ left: 0, top: 0 })
      }
      return
    }
    if (envelope.event) applyEvent(envelope.event)
  }, [applyEvent, cancelMotions])

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
