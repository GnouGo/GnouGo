export type GnouGnouAnimationName =
  | 'idle'
  | 'walk'
  | 'arrive'
  | 'pickup'
  | 'handoff'
  | 'type'
  | 'wait'
  | 'deliver'
  | 'think'
  | 'communicate'
  | 'write'
  | 'plan'
  | 'ask'
  | 'build'
  | 'clone'
  | 'merge'
  | 'celebrate'
  | 'fail'

export const GNOUNOU_ANIMATIONS: readonly GnouGnouAnimationName[] = [
  'idle',
  'walk',
  'arrive',
  'pickup',
  'handoff',
  'type',
  'wait',
  'deliver',
  'think',
  'communicate',
  'write',
  'plan',
  'ask',
  'build',
  'clone',
  'merge',
  'celebrate',
  'fail',
]

interface RigTransform {
  angle: number
  x: number
  y: number
  scaleX: number
  scaleY: number
}

interface AmbientLife {
  breath: number
  look: number
  blink: number
  leftEar: number
  rightEar: number
  mouth: number
  yawn: number
  drift: number
  gesture: number
  accent: number
  rhythm: number
  idleVariant: GnouGnouIdleVariant
}

export const GNOUNOU_IDLE_VARIANTS = [
  'look-around',
  'side-sway',
  'stretch',
  'toe-tap',
  'ponder',
  'little-wave',
] as const

export type GnouGnouIdleVariant = typeof GNOUNOU_IDLE_VARIANTS[number]

interface IdleIdentity {
  variant: GnouGnouIdleVariant
  phase: number
  timeOffsetSeconds: number
}

type SvgPart = SVGGraphicsElement | null

function rigPart(actor: SVGGraphicsElement, name: string): SvgPart {
  return actor.querySelector<SVGGraphicsElement>(`[data-part="${name}"]`)
}

function applyRigTransform(
  part: SvgPart,
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

function applyThinkingCheekColor(cheek: SvgPart, intensity: number) {
  const blush = cheek?.querySelector<SVGEllipseElement>('ellipse')
  if (!blush) return
  const amount = Math.max(0, Math.min(1, intensity))
  const red = Math.round(247 - amount * 14)
  const green = Math.round(154 - amount * 82)
  const blue = Math.round(160 - amount * 78)
  blush.setAttribute('fill', `rgb(${red},${green},${blue})`)
  blush.setAttribute('opacity', String(.68 + amount * .22))
}

function periodicPulse(seconds: number, period: number, phaseOffset: number, halfWidth: number): number {
  const phase = ((seconds / period + phaseOffset) % 1 + 1) % 1
  const distance = Math.min(Math.abs(phase - .5), 1 - Math.abs(phase - .5))
  if (distance >= halfWidth) return 0
  const value = 1 - distance / halfWidth
  return value * value * (3 - 2 * value)
}

function gaitPulse(phase: number, start: number): number {
  const local = ((phase - start) % 1 + 1) % 1
  return local < .46 ? Math.sin(local / .46 * Math.PI) : 0
}

function seededUnit(seed: number, salt: number): number {
  let value = (Math.trunc(seed) ^ Math.imul(salt, 0x9e3779b9)) >>> 0
  value = Math.imul(value ^ value >>> 16, 0x21f0aaad)
  value = Math.imul(value ^ value >>> 15, 0x735a2d97)
  return ((value ^ value >>> 15) >>> 0) / 0x1_0000_0000
}

function ambientLifeAt(now: number, seed: number, identity: IdleIdentity): AmbientLife {
  const seconds = now / 1000 + identity.timeOffsetSeconds
  const stableSeed = Math.abs(seed || 1)
  const breathPeriod = 6.8 + stableSeed % 6 * .22
  const lookPeriod = 12.5 + stableSeed % 8 * .37
  const phase = identity.phase
  const gesturePeriod = 13 + seededUnit(stableSeed, 31) * 9
  const accentPeriod = 17 + seededUnit(stableSeed, 43) * 13
  const gesture = periodicPulse(seconds, gesturePeriod, seededUnit(stableSeed, 37), .14)
  return {
    breath: Math.sin((seconds / breathPeriod + phase) * Math.PI * 2),
    look: Math.sin((seconds / lookPeriod + phase * .7) * Math.PI * 2),
    blink: periodicPulse(seconds, 6.1 + stableSeed % 7 * .43, phase * .83, .018),
    leftEar: periodicPulse(seconds, 9.5 + stableSeed % 5 * .71, phase * .61, .055),
    rightEar: periodicPulse(seconds, 11.3 + stableSeed % 7 * .63, phase * .37 + .29, .05),
    mouth: Math.sin((seconds / (9.2 + stableSeed % 5 * .41) + phase) * Math.PI * 2),
    yawn: periodicPulse(seconds, 48 + stableSeed % 19, phase * .47 + .13, .048),
    drift: Math.sin((seconds / (8.5 + seededUnit(stableSeed, 47) * 5) + phase * .53) * Math.PI * 2),
    gesture,
    accent: periodicPulse(seconds, accentPeriod, seededUnit(stableSeed, 53), .1),
    rhythm: Math.sin(seconds * Math.PI * (2.2 + seededUnit(stableSeed, 59) * 1.4)) * gesture,
    idleVariant: identity.variant,
  }
}

/**
 * Bears-owned controller for semantic GnOuGo SVG rigs.
 *
 * The host chooses when an action starts. This controller owns all body-part
 * transforms, ambient life, reduced-motion handling, pose composition, and
 * cancellation semantics.
 */
export class GnouGnouAnimationController {
  private readonly poseFrames = new Map<string, number>()
  private readonly idleIdentities = new WeakMap<SVGGraphicsElement, IdleIdentity>()
  private readonly idleVariantUsage = GNOUNOU_IDLE_VARIANTS.map(() => 0)
  private ambientFrame: number | null = null
  private idleIdentityOrdinal = 0
  private generation = 0

  constructor(private readonly root: () => ParentNode | null) {}

  startAmbient() {
    if (this.ambientFrame !== null) return
    const render = (now: number) => {
      if (!window.matchMedia('(prefers-reduced-motion: reduce)').matches) {
        this.root()?.querySelectorAll<SVGGraphicsElement>('.gnougo-actor[data-visible="true"], .gnougo-rig[data-animation-rig="true"]').forEach(actor => {
          if (actor.matches('.gnougo-rig') && actor.closest('.gnougo-actor')) return
          const pose = actor.getAttribute('data-pose') ?? 'idle'
          if (pose !== 'idle' && pose !== 'fail') return
          const seed = Number(actor.getAttribute('data-visual-seed') ?? 1)
          this.renderAmbientPose(actor, pose, this.ambientLife(actor, now, seed))
        })
      }
      this.ambientFrame = requestAnimationFrame(render)
    }
    this.ambientFrame = requestAnimationFrame(render)
  }

  stopAmbient() {
    if (this.ambientFrame !== null)
      cancelAnimationFrame(this.ambientFrame)
    this.ambientFrame = null
  }

  cancelAll(resetToIdle = true) {
    this.generation += 1
    this.poseFrames.forEach(frame => cancelAnimationFrame(frame))
    this.poseFrames.clear()
    if (!resetToIdle) return
    this.root()?.querySelectorAll<SVGGraphicsElement>('.gnougo-actor, .gnougo-rig[data-animation-rig="true"]').forEach(actor => {
      if (actor.matches('.gnougo-rig') && actor.closest('.gnougo-actor')) return
      this.resetActor(actor)
    })
  }

  /**
   * Stops one articulated pose without moving the actor. This is useful when
   * an external item reaches a waiting character: the rig remains exactly at
   * its current transform until the next authoritative action starts.
   */
  stop(actorId: string, resetToIdle = false) {
    const frame = this.poseFrames.get(actorId)
    if (frame !== undefined) cancelAnimationFrame(frame)
    this.poseFrames.delete(actorId)
    const actor = this.findActor(actorId)
    if (!actor) return
    if (resetToIdle) {
      this.resetActor(actor)
      return
    }
    actor.setAttribute('data-pose', 'hold')
  }

  reset(actorId: string) {
    const frame = this.poseFrames.get(actorId)
    if (frame !== undefined) cancelAnimationFrame(frame)
    this.poseFrames.delete(actorId)
    const actor = this.findActor(actorId)
    if (actor) this.resetActor(actor)
  }

  play(actorId: string | undefined, action: GnouGnouAnimationName, duration: number, direction = 1) {
    if (!actorId) return
    const actor = this.findActor(actorId)
    const hasRig = actor?.matches('[data-animation-rig="true"]')
      || actor?.querySelector('[data-animation-rig="true"]') !== null
    if (!actor || !hasRig) return
    const previousFrame = this.poseFrames.get(actorId)
    if (previousFrame !== undefined) cancelAnimationFrame(previousFrame)

    const reduced = window.matchMedia('(prefers-reduced-motion: reduce)').matches
    const actualDuration = reduced ? 1 : Math.max(360, duration * 1.15)
    const generation = this.generation
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
    const thinkingFlush = rigPart(actor, 'thinking-flush')
    const thinkingSweat = rigPart(actor, 'thinking-sweat')
    const thinkingArmRub = rigPart(actor, 'thinking-arm-rub')
    const thinkingHandRub = rigPart(actor, 'thinking-hand-rub')
    const visualSeed = Number(actor.getAttribute('data-visual-seed') ?? 1)
    this.setFailureExpression(actor, action === 'fail')
    const initialTransforms = new Map<SVGGraphicsElement, RigTransform>()
    actor.querySelectorAll<SVGGraphicsElement>('[data-part]').forEach(part => {
      initialTransforms.set(part, readRigTransform(part))
    })
    const targets = new Map<SVGGraphicsElement, RigTransform>()
    let poseBlend = 0
    const set = (part: SvgPart, angle = 0, x = 0, y = 0, scaleX = 1, scaleY = 1) => {
      if (part) targets.set(part, { angle, x, y, scaleX, scaleY })
    }
    const add = (part: SvgPart, angle = 0, x = 0, y = 0, scaleX = 0, scaleY = 0) => {
      if (!part) return
      const target = targets.get(part) ?? { angle: 0, x: 0, y: 0, scaleX: 1, scaleY: 1 }
      targets.set(part, {
        angle: target.angle + angle,
        x: target.x + x,
        y: target.y + y,
        scaleX: target.scaleX * (1 + scaleX),
        scaleY: target.scaleY * (1 + scaleY),
      })
    }

    const render = (now: number) => {
      if (generation !== this.generation || !actor.isConnected) return
      const progress = Math.max(0, Math.min(1, (now - startedAt) / actualDuration))
      const gesture = Math.sin(progress * Math.PI)
      const elapsedSeconds = (now - startedAt) / 1000
      poseBlend = Math.min(1, elapsedSeconds / .22)
      const wave = Math.sin(elapsedSeconds * Math.PI * 1.9) * gesture
      const fastWave = Math.sin(elapsedSeconds * Math.PI * 2.2) * gesture
      const bounce = Math.abs(wave)
      const gaitPhase = elapsedSeconds / 1.65 % 1
      const leftStep = gaitPulse(gaitPhase, 0) * gesture
      const rightStep = gaitPulse(gaitPhase, .5) * gesture
      const stepLift = Math.max(leftStep, rightStep)
      const stepBalance = leftStep - rightStep
      const life = this.ambientLife(actor, now, visualSeed)
      const actionBlink = actualDuration >= 700
        ? Math.max(0, 1 - Math.abs(progress - .62) / .035)
        : 0
      const ambientStrength = action === 'type' || action === 'wait' || action === 'think' ? .72 : .34
      let eyeScale = 1 - Math.max(actionBlink, life.blink * ambientStrength, life.yawn * .52) * .9
      let pupilX = direction * 1.4
      let pupilY = 0
      let effectOpacity = 0
      let thinkingFlushOpacity = 0
      let thinkingSweatOpacity = 0
      let thinkingCheekIntensity = 0
      let thinkingArmOpacity = 0

      set(leftArm)
      set(rightArm)
      set(leftEar, wave * 2)
      set(rightEar, -wave * 2)
      set(leftLeg)
      set(rightLeg)
      set(body)
      set(head)
      set(leftBrow, -2)
      set(rightBrow, 2)
      set(leftCheek)
      set(rightCheek)
      set(thinkingArmRub)
      set(thinkingHandRub)
      set(mouth, 0, 0, -1, 1.04, 1.08)
      set(bowTie)

      switch (action) {
        case 'idle':
          break
        case 'walk':
          set(leftLeg, leftStep * 28, 0, -leftStep * 3)
          set(rightLeg, -rightStep * 28, 0, -rightStep * 3)
          set(leftArm, rightStep * 18 - leftStep * 5)
          set(rightArm, -leftStep * 18 + rightStep * 5)
          set(body, stepBalance * .35, 0, -stepLift * .45)
          set(head, -stepBalance * .55, direction * .8, -stepLift * .5)
          set(leftEar, 3 + leftStep * 3.5)
          set(rightEar, -3 - rightStep * 3.5)
          set(leftCheek, 0, 0, 0, 1 + stepLift * .035, 1 + stepLift * .035)
          set(rightCheek, 0, 0, 0, 1 + stepLift * .035, 1 + stepLift * .035)
          set(mouth, stepBalance * .65, 0, -1, 1.05, 1.12)
          break
        case 'arrive':
          set(leftArm, 18 * gesture)
          set(rightArm, -68 * gesture + fastWave * 5)
          set(head, -direction * 4 * gesture, 0, -bounce)
          set(leftEar, 10 * gesture + fastWave * 2)
          set(rightEar, -10 * gesture - fastWave * 2)
          set(leftBrow, -8 * gesture)
          set(rightBrow, 8 * gesture)
          set(leftCheek, 0, 0, 0, 1 + gesture * .12, 1 + gesture * .12)
          set(rightCheek, 0, 0, 0, 1 + gesture * .12, 1 + gesture * .12)
          set(mouth, 0, 0, -gesture * 2, 1 + gesture * .14, 1 + gesture * .22)
          effectOpacity = gesture * .65
          break
        case 'pickup':
          set(leftArm, -41 * gesture)
          set(rightArm, 41 * gesture)
          set(leftLeg, 10 * gesture)
          set(rightLeg, -10 * gesture)
          set(body, 0, 0, 10 * gesture, 1, 1 - .06 * gesture)
          set(head, direction * 2.5 * gesture, 0, 9 * gesture)
          set(leftEar, -7 * gesture)
          set(rightEar, 7 * gesture)
          pupilY = 4 * gesture
          break
        case 'handoff':
          set(leftArm, -43 * gesture + fastWave * 1.5)
          set(rightArm, 43 * gesture - fastWave * 1.5)
          set(body, direction * 3 * gesture, direction * 3 * gesture)
          set(head, direction * 5 * gesture, direction * 2 * gesture)
          set(leftEar, direction * 10 * gesture)
          set(rightEar, direction * 5 * gesture)
          pupilX = direction * 3
          effectOpacity = gesture * .55
          break
        case 'type':
          set(leftArm, -34 * gesture + fastWave * 3.5)
          set(rightArm, 34 * gesture - fastWave * 3.5)
          set(leftLeg, wave * 1.5)
          set(rightLeg, -wave * 1.5)
          set(body, wave * .6, 0, bounce * .7)
          set(head, wave, 0, 3.5 * gesture + bounce * .5)
          set(leftEar, 3 + fastWave * 2.5)
          set(rightEar, -3 - fastWave * 2.5)
          set(leftBrow, -2 + fastWave * .7)
          set(rightBrow, 2 - fastWave * .7)
          set(leftCheek, 0, 0, 0, 1 + bounce * .06, 1 + bounce * .06)
          set(rightCheek, 0, 0, 0, 1 + bounce * .06, 1 + bounce * .06)
          set(mouth, fastWave * 1.2, 0, -1, 1.06, 1.13)
          pupilX = fastWave * .8
          pupilY = 3 * gesture
          effectOpacity = .12 + bounce * .22
          break
        case 'wait':
          set(leftArm, 12 * gesture + wave * 1.5)
          set(rightArm, -12 * gesture - wave * 1.5)
          set(body, wave * .6, 0, -bounce * .5)
          set(head, direction * (2 + wave), 0, -bounce * .5)
          set(leftEar, wave * 4)
          set(rightEar, -wave * 4)
          pupilX = direction * (1.3 + wave)
          break
        case 'deliver':
          set(leftArm, 88 * gesture + fastWave * 2)
          set(rightArm, -88 * gesture - fastWave * 2)
          set(leftLeg, fastWave * 2)
          set(rightLeg, -fastWave * 2)
          set(body, 0, 0, -gesture * 8)
          set(head, -fastWave, 0, -gesture * 4)
          set(leftEar, 10 * gesture + fastWave * 2)
          set(rightEar, -10 * gesture - fastWave * 2)
          set(leftBrow, -10 * gesture)
          set(rightBrow, 10 * gesture)
          set(leftCheek, 0, 0, 0, 1 + gesture * .16, 1 + gesture * .16)
          set(rightCheek, 0, 0, 0, 1 + gesture * .16, 1 + gesture * .16)
          set(mouth, 0, 0, -gesture * 3, 1 + gesture * .18, 1 + gesture * .3)
          pupilY = -2.5 * gesture
          effectOpacity = .4 + .6 * bounce
          break
        case 'think': {
          const cheekPulse = (.5 + Math.sin(elapsedSeconds * Math.PI * 1.35) * .5) * gesture
          const headRub = Math.sin(elapsedSeconds * Math.PI * 2.5) * gesture
          set(leftArm, -22 * gesture)
          set(body, wave * .35, 0, gesture * 2, 1, 1 - gesture * .012)
          set(head, -5 * gesture + wave * .7, 0, gesture)
          set(thinkingArmRub, -154 - headRub * 9)
          set(thinkingHandRub, -headRub * 6, 0, 0, 1.02, 1.02)
          set(leftBrow, 10 * gesture)
          set(rightBrow, -10 * gesture)
          set(
            leftCheek,
            0,
            0,
            0,
            1 + gesture * .1 + cheekPulse * .1,
            1 + gesture * .05 + (gesture - cheekPulse) * .09,
          )
          set(
            rightCheek,
            0,
            0,
            0,
            1 + gesture * .11 + (gesture - cheekPulse) * .08,
            1 + gesture * .05 + cheekPulse * .09,
          )
          eyeScale *= .72 + bounce * .12
          pupilX = fastWave * 2.2
          pupilY = -2.8 * gesture
          thinkingFlushOpacity = gesture * (.2 + bounce * .22)
          thinkingSweatOpacity = gesture * (.48 + bounce * .5)
          thinkingCheekIntensity = gesture * (.72 + bounce * .28)
          thinkingArmOpacity = Math.min(1, gesture * 1.8)
          break
        }
        case 'communicate':
          set(leftArm, -26 * gesture + fastWave * 3)
          set(rightArm, -94 * gesture + fastWave * 3)
          set(head, wave * 1.5)
          pupilX = wave * 2
          effectOpacity = .35 + .65 * bounce
          break
        case 'write':
          set(leftArm, -34 * gesture + fastWave * 2.5)
          set(rightArm, 34 * gesture - fastWave * 2.5)
          set(head, wave, 0, 4 * gesture + bounce * .5)
          set(body, 0, 0, bounce * .7)
          pupilX = fastWave
          pupilY = 3 * gesture
          break
        case 'plan':
          set(leftArm, 75 * gesture + fastWave * 2.5)
          set(rightArm, -24 * gesture)
          set(head, -direction * 4 * gesture)
          pupilX = direction * 3 * gesture
          pupilY = -2 * gesture
          effectOpacity = .2 + .7 * bounce
          break
        case 'ask':
          set(leftArm, -22 * gesture)
          set(rightArm, -92 * gesture + fastWave * 7)
          set(head, direction * (3 + wave) * gesture)
          pupilX = direction * 3
          effectOpacity = gesture * .7
          break
        case 'build':
          set(leftArm, -38 * gesture)
          set(rightArm, -58 * gesture + fastWave * 13)
          set(head, -fastWave, 0, bounce)
          set(body, fastWave * .7)
          effectOpacity = bounce * .45
          break
        case 'clone':
          set(leftArm, 70 * gesture)
          set(rightArm, -70 * gesture)
          set(leftLeg, -14 * gesture)
          set(rightLeg, 14 * gesture)
          set(head, fastWave * 2, 0, -gesture * 7)
          set(leftEar, 15 * gesture + fastWave * 2.5)
          set(rightEar, -15 * gesture - fastWave * 2.5)
          set(body, 0, 0, -gesture * 5, 1 + gesture * .08, 1 + gesture * .08)
          effectOpacity = .35 + .65 * bounce
          break
        case 'merge':
          set(leftArm, -44 * gesture)
          set(rightArm, 44 * gesture)
          set(head, -fastWave * 1.3)
          effectOpacity = gesture
          break
        case 'celebrate':
          set(leftArm, 90 * gesture + fastWave * 3)
          set(rightArm, -90 * gesture - fastWave * 3)
          set(leftLeg, fastWave * 3)
          set(rightLeg, -fastWave * 3)
          set(body, fastWave * .8, 0, -bounce * 5)
          set(head, -fastWave * 1.5, 0, -bounce * 3)
          set(leftEar, 13 * gesture + fastWave * 4)
          set(rightEar, -13 * gesture - fastWave * 4)
          set(leftBrow, -12 * gesture)
          set(rightBrow, 12 * gesture)
          set(leftCheek, 0, 0, 0, 1 + gesture * .2, 1 + gesture * .2)
          set(rightCheek, 0, 0, 0, 1 + gesture * .2, 1 + gesture * .2)
          set(mouth, 0, 0, -gesture * 2, 1 + gesture * .18, 1 + gesture * .28)
          set(bowTie, fastWave * 10)
          pupilY = -2 * gesture
          effectOpacity = .45 + .55 * bounce
          break
        case 'fail': {
          const slump = Math.min(1, progress * 3)
          set(leftArm, -8 * slump)
          set(rightArm, 8 * slump)
          set(leftLeg, 6 * slump)
          set(rightLeg, -6 * slump)
          set(body, 0, 0, 7 * slump, 1, 1 - .05 * slump)
          set(head, 12 * slump, 0, 11 * slump)
          set(leftEar, -16 * slump)
          set(rightEar, 16 * slump)
          set(leftBrow, 10 * slump)
          set(rightBrow, -10 * slump)
          set(leftCheek, 0, 0, slump, 1 - slump * .08, 1 - slump * .08)
          set(rightCheek, 0, 0, slump, 1 - slump * .08, 1 - slump * .08)
          set(mouth, 0, 0, 3 * slump, 1, .7)
          pupilX = 0
          pupilY = 5 * slump
          break
        }
      }

      set(leftEye, 0, 0, 0, 1, eyeScale)
      set(rightEye, 0, 0, 0, 1, eyeScale)
      set(leftPupil, 0, pupilX + life.look * .55 * ambientStrength, pupilY + life.breath * .12)
      set(rightPupil, 0, pupilX + life.look * .55 * ambientStrength, pupilY + life.breath * .12)
      add(body, life.breath * .12 * ambientStrength, 0, -Math.abs(life.breath) * .2, 0, life.breath * .002)
      add(head, life.look * .32 * ambientStrength - life.yawn * 2.2, 0, -Math.abs(life.breath) * .18)
      add(leftEar, life.leftEar * 3.8 * ambientStrength + life.breath * .35)
      add(rightEar, -life.rightEar * 3.4 * ambientStrength - life.breath * .28)
      add(
        mouth,
        life.mouth * .35 * ambientStrength,
        0,
        life.yawn * 1.8,
        life.mouth * .012 * ambientStrength,
        life.yawn * 1.15 + Math.abs(life.mouth) * .018 * ambientStrength,
      )
      if ((action === 'wait' || action === 'think') && life.yawn > 0) {
        add(rightArm, -36 * life.yawn)
        add(leftArm, 5 * life.yawn)
      }

      const blend = poseBlend * poseBlend * (3 - 2 * poseBlend)
      targets.forEach((target, part) => {
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
      if (thinkingFlush)
        thinkingFlush.setAttribute('opacity', String(thinkingFlushOpacity))
      if (thinkingSweat) {
        thinkingSweat.setAttribute('opacity', String(thinkingSweatOpacity))
        thinkingSweat.setAttribute('transform', `translate(0 ${-4 + bounce * 8}) scale(${.82 + gesture * .18})`)
      }
      applyThinkingCheekColor(leftCheek, thinkingCheekIntensity)
      applyThinkingCheekColor(rightCheek, thinkingCheekIntensity)
      if (thinkingArmRub) thinkingArmRub.setAttribute('opacity', String(thinkingArmOpacity))
      if (rightArm) rightArm.setAttribute('opacity', String(1 - thinkingArmOpacity))

      if (progress < 1) {
        const frame = requestAnimationFrame(render)
        this.poseFrames.set(actorId, frame)
        return
      }

      this.poseFrames.delete(actorId)
      if (action === 'fail')
        actor.setAttribute('data-pose', 'fail')
      else
        this.resetActor(actor)
    }

    const frame = requestAnimationFrame(render)
    this.poseFrames.set(actorId, frame)
  }

  private findActor(actorId: string): SVGGraphicsElement | null {
    const root = this.root()
    if (!root) return null
    if (typeof SVGGraphicsElement !== 'undefined'
      && root instanceof SVGGraphicsElement
      && root.id === actorId)
      return root
    const escape = globalThis.CSS?.escape
    if (escape) {
      try {
        return root.querySelector<SVGGraphicsElement>(`#${escape(actorId)}`)
      } catch {
        // Fall through to an exact id comparison for older embedded webviews.
      }
    }
    return Array.from(root.querySelectorAll<SVGGraphicsElement>('[id]'))
      .find(element => element.id === actorId) ?? null
  }

  private resetActor(actor: SVGGraphicsElement) {
    actor.querySelectorAll<SVGGraphicsElement>('[data-part]').forEach(part => part.removeAttribute('transform'))
    this.setFailureExpression(actor, false)
    const effects = rigPart(actor, 'action-fx')
    if (effects) effects.setAttribute('opacity', '0')
    const thinkingFlush = rigPart(actor, 'thinking-flush')
    if (thinkingFlush) thinkingFlush.setAttribute('opacity', '0')
    const thinkingSweat = rigPart(actor, 'thinking-sweat')
    if (thinkingSweat) thinkingSweat.setAttribute('opacity', '0')
    const thinkingArmRub = rigPart(actor, 'thinking-arm-rub')
    if (thinkingArmRub) thinkingArmRub.setAttribute('opacity', '0')
    const rightArm = rigPart(actor, 'arm-right')
    if (rightArm) rightArm.setAttribute('opacity', '1')
    applyThinkingCheekColor(rigPart(actor, 'cheek-left'), 0)
    applyThinkingCheekColor(rigPart(actor, 'cheek-right'), 0)
    actor.setAttribute('data-pose', 'idle')
  }

  private ambientLife(actor: SVGGraphicsElement, now: number, seed: number): AmbientLife {
    let identity = this.idleIdentities.get(actor)
    if (!identity) {
      const preferred = Math.floor(seededUnit(seed, 17) * GNOUNOU_IDLE_VARIANTS.length)
      let variantIndex = preferred
      for (let offset = 1; offset < GNOUNOU_IDLE_VARIANTS.length; offset += 1) {
        const candidate = (preferred + offset) % GNOUNOU_IDLE_VARIANTS.length
        if (this.idleVariantUsage[candidate] < this.idleVariantUsage[variantIndex])
          variantIndex = candidate
      }
      const ordinal = this.idleIdentityOrdinal++
      const phase = (seededUnit(seed, 23) + ordinal * .61803398875) % 1
      identity = {
        variant: GNOUNOU_IDLE_VARIANTS[variantIndex],
        phase,
        timeOffsetSeconds: 3 + seededUnit(seed, 29) * 41 + ordinal * 1.37,
      }
      this.idleIdentities.set(actor, identity)
      this.idleVariantUsage[variantIndex] += 1
      actor.setAttribute('data-idle-variant', identity.variant)
      actor.setAttribute('data-idle-offset-ms', String(Math.round(identity.timeOffsetSeconds * 1000)))
    }
    return ambientLifeAt(now, seed, identity)
  }

  private setFailureExpression(actor: SVGGraphicsElement, failed: boolean) {
    actor.querySelector<SVGGraphicsElement>('[data-expression="default"]')
      ?.setAttribute('opacity', failed ? '0' : '1')
    actor.querySelector<SVGGraphicsElement>('[data-expression="failure"]')
      ?.setAttribute('opacity', failed ? '1' : '0')
  }

  private renderAmbientPose(actor: SVGGraphicsElement, pose: string, life: AmbientLife) {
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
    let bodyAngle = life.breath * .22
    let bodyX = 0
    let bodyY = -Math.abs(life.breath) * .45
    let headAngle = life.look * .62 - life.yawn * 3.8
    let headX = 0
    let headY = -Math.abs(life.breath) * .32 - life.yawn * 1.2
    let leftArmAngle = 5 * life.yawn
    let rightArmAngle = -58 * life.yawn
    let leftLegAngle = 0
    let rightLegAngle = 0
    let rightLegY = 0
    let leftEarAngle = life.leftEar * 7 + life.breath * .55 - life.yawn * 2
    let rightEarAngle = -life.rightEar * 6 - life.breath * .45 + life.yawn * 2
    let pupilX = life.look * .9
    let pupilY = life.breath * .16
    let leftBrowAngle = 0
    let rightBrowAngle = 0
    let bowTieAngle = life.drift * .35

    switch (life.idleVariant) {
      case 'look-around':
        headAngle += life.drift * 2.4 + life.gesture * 2.8
        headX += life.drift * .65
        pupilX += life.drift * 1.6
        leftEarAngle += life.gesture * 5
        rightEarAngle -= life.accent * 4
        leftBrowAngle -= life.gesture * 4
        rightBrowAngle += life.gesture * 1.5
        break
      case 'side-sway':
        bodyAngle += life.drift * 1.15
        bodyX += life.drift * .75
        headAngle -= life.drift * 1.7
        leftArmAngle += life.drift * 3.2
        rightArmAngle += life.drift * 3.2
        leftLegAngle -= life.drift * 1.2
        rightLegAngle -= life.drift * 1.2
        bowTieAngle += life.drift * 1.6
        break
      case 'stretch': {
        const stretch = Math.max(life.gesture, life.accent * .72)
        leftArmAngle += stretch * 43
        rightArmAngle -= stretch * 43
        bodyY -= stretch * 2.8
        headY -= stretch * 2
        headAngle += life.drift * .8
        leftEarAngle += stretch * 4
        rightEarAngle -= stretch * 4
        bowTieAngle += life.rhythm * 2
        break
      }
      case 'toe-tap': {
        const tap = Math.abs(life.rhythm)
        rightLegAngle -= life.rhythm * 9
        rightLegY -= tap * 2.4
        bodyAngle += life.rhythm * .45
        headAngle -= life.rhythm * .6
        pupilY += tap * .35
        rightEarAngle -= tap * 2
        break
      }
      case 'ponder':
        rightArmAngle += life.gesture * 58
        leftArmAngle -= life.gesture * 7
        headAngle -= life.gesture * 5 + life.drift * 1.1
        pupilX += life.gesture * 1.8
        pupilY -= life.gesture * 1.4
        leftBrowAngle -= life.gesture * 5
        rightBrowAngle += life.gesture * 3
        break
      case 'little-wave':
        rightArmAngle -= life.gesture * (45 + life.rhythm * 11)
        leftArmAngle += life.gesture * 5
        headAngle += life.gesture * 2.4 + life.drift * .7
        headX += life.gesture * .6
        pupilX += life.gesture * 1.1
        leftEarAngle += life.gesture * 3
        rightEarAngle -= life.gesture * 5
        bowTieAngle += life.rhythm * 2.5
        break
    }

    applyRigTransform(rigPart(actor, 'body'), bodyAngle, bodyX, bodyY, 1, 1 + life.breath * .004)
    applyRigTransform(rigPart(actor, 'head'), headAngle, headX, headY)
    applyRigTransform(rigPart(actor, 'arm-left'), leftArmAngle)
    applyRigTransform(rigPart(actor, 'arm-right'), rightArmAngle)
    applyRigTransform(rigPart(actor, 'leg-left'), leftLegAngle)
    applyRigTransform(rigPart(actor, 'leg-right'), rightLegAngle, 0, rightLegY)
    applyRigTransform(rigPart(actor, 'ear-left'), leftEarAngle)
    applyRigTransform(rigPart(actor, 'ear-right'), rightEarAngle)
    applyRigTransform(rigPart(actor, 'eye-left'), 0, 0, 0, 1, 1 - eyeClose * .9)
    applyRigTransform(rigPart(actor, 'eye-right'), 0, 0, 0, 1, 1 - eyeClose * .9)
    applyRigTransform(rigPart(actor, 'pupil-left'), 0, pupilX, pupilY)
    applyRigTransform(rigPart(actor, 'pupil-right'), 0, pupilX, pupilY)
    applyRigTransform(rigPart(actor, 'brow-left'), leftBrowAngle)
    applyRigTransform(rigPart(actor, 'brow-right'), rightBrowAngle)
    applyRigTransform(rigPart(actor, 'bow-tie'), bowTieAngle)
    applyRigTransform(
      rigPart(actor, 'mouth'),
      life.mouth * .42,
      0,
      -1 + life.yawn * 2.4,
      1.04 + life.mouth * .012,
      1.08 + life.yawn * 1.42 + Math.abs(life.mouth) * .02,
    )
  }
}
