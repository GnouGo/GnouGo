// ── Pre-filled example workflow — Dungeon Dice Adventure 🎲 ──

export const EXAMPLE_WORKFLOW = `dsl: 1
name: dungeon-dice-adventure
meta:
  description: >
    A mini RPG dice game! Your hero fights a series of monsters using
    WFScript functions (Math.random via Jint), templates, loops, switch
    and an LLM narrator — no MCP needed.

functions: |
  function rollDice(sides) {
    return Math.floor(Math.random() * sides) + 1;
  }
  function rollAttack(base_atk) {
    var roll = rollDice(20);
    return { roll: roll, total: roll + base_atk, critical: roll === 20 };
  }
  function rollDefense(base_def) {
    var roll = rollDice(20);
    return { roll: roll, total: roll + base_def };
  }
  function computeDamage(atk, def, weapon_bonus) {
    var diff = atk.total - def.total;
    if (atk.critical) diff = diff + weapon_bonus * 2;
    if (diff < 0) diff = 0;
    var dmg = diff + rollDice(6);
    return dmg;
  }
  function pickRandom(arr) {
    return arr[Math.floor(Math.random() * arr.length)];
  }
  function clamp(val, lo, hi) {
    if (val < lo) return lo;
    if (val > hi) return hi;
    return val;
  }

workflows:
  main:
    inputs:
      hero_name:
        type: string
        required: false
        default: Aldric
        description: Name of the brave hero
      hero_class:
        type: string
        required: false
        default: knight
        description: "Hero class: knight, mage, or rogue"
      difficulty:
        type: string
        required: false
        default: normal
        description: "Difficulty: easy, normal, or hard"
      monsters:
        type: array
        required: false
        items: { type: string }
        default:
          - Goblin
          - Skeleton
          - Dark Elf
          - Stone Golem
        description: List of monsters to fight

    steps:
      # ── 1. Compute hero stats based on class ──
      - id: hero_stats
        type: set
        input:
          attack: '\${data.inputs.hero_class == "mage" ? 3 : (data.inputs.hero_class == "rogue" ? 5 : 4)}'
          defense: '\${data.inputs.hero_class == "mage" ? 2 : (data.inputs.hero_class == "rogue" ? 3 : 4)}'
          hp: '\${data.inputs.hero_class == "mage" ? 30 : (data.inputs.hero_class == "rogue" ? 25 : 40)}'
          weapon: '\${data.inputs.hero_class == "mage" ? "Staff of Thunder" : (data.inputs.hero_class == "rogue" ? "Shadow Daggers" : "Excalibur")}'
          weapon_bonus: '\${data.inputs.hero_class == "mage" ? 4 : (data.inputs.hero_class == "rogue" ? 3 : 2)}'
          special: '\${data.inputs.hero_class == "mage" ? "Fireball" : (data.inputs.hero_class == "rogue" ? "Backstab" : "Shield Bash")}'

      - id: difficulty_mult
        type: set
        input:
          mult: '\${data.inputs.difficulty == "hard" ? 2 : (data.inputs.difficulty == "easy" ? 0.5 : 1)}'

      - id: think_intro
        type: emit
        input:
          message: "⚔️ \${data.inputs.hero_name} the \${upper(data.inputs.hero_class)} enters the dungeon with \${data.steps.hero_stats.hp} HP and \${data.steps.hero_stats.weapon}!"
          level: info

      # ── 2. Fight each monster in sequence ──
      - id: think_combat
        type: emit
        input:
          message: "Engaging \${len(data.inputs.monsters)} monsters on \${data.inputs.difficulty} difficulty…"
          level: thinking

      - id: battles
        type: loop.parallel
        input:
          items: "\${data.inputs.monsters}"
          max_concurrency: 1
        item_var: monster
        index_var: round
        steps:
          - id: think_round
            type: emit
            input:
              message: "⚔️ Round \${data.round + 1}: \${data.inputs.hero_name} vs \${data.monster}!"
              level: thinking

          - id: monster_hp
            type: set
            input:
              hp: "\${functions.clamp(functions.rollDice(20) * data.steps.difficulty_mult.mult, 5, 50)}"

          - id: hero_attack
            type: set
            input:
              roll: "\${functions.rollAttack(data.steps.hero_stats.attack)}"

          - id: monster_defense
            type: set
            input:
              roll: "\${functions.rollDefense(functions.rollDice(4))}"

          - id: hero_damage
            type: set
            input:
              value: "\${functions.computeDamage(data.steps.hero_attack.roll, data.steps.monster_defense.roll, data.steps.hero_stats.weapon_bonus)}"

          - id: monster_attack
            type: set
            input:
              roll: "\${functions.rollAttack(functions.rollDice(6))}"

          - id: hero_defense
            type: set
            input:
              roll: "\${functions.rollDefense(data.steps.hero_stats.defense)}"

          - id: monster_damage
            type: set
            input:
              value: "\${functions.computeDamage(data.steps.monster_attack.roll, data.steps.hero_defense.roll, 1)}"

          - id: outcome
            type: set
            input:
              verdict: "\${data.steps.hero_attack.roll.critical ? 'critical_hit' : (data.steps.hero_damage.value > data.steps.monster_hp.hp ? 'victory' : (data.steps.hero_damage.value == 0 ? 'miss' : 'hit'))}"
              label: "\${data.steps.hero_attack.roll.critical ? '💥 CRITICAL HIT!' : (data.steps.hero_damage.value > data.steps.monster_hp.hp ? '✅ Victory!' : (data.steps.hero_damage.value == 0 ? '😬 Missed!' : '⚔️ Hit!'))}"

          - id: round_summary
            type: template.render
            input:
              engine: mustache
              template: |
                === Round {{round}} — {{hero}} vs {{monster}} ===
                Hero rolls: atk {{atk_roll}} (total {{atk_total}}) | Monster def: {{def_total}}
                Damage dealt: {{hero_dmg}} / Monster HP: {{m_hp}} → {{verdict}}
                Monster rolls: atk {{m_atk_total}} | Hero def: {{h_def_total}}
                Damage taken: {{m_dmg}}
              data:
                round: "\${data.round + 1}"
                hero: "\${data.inputs.hero_name}"
                monster: "\${data.monster}"
                atk_roll: "\${data.steps.hero_attack.roll.roll}"
                atk_total: "\${data.steps.hero_attack.roll.total}"
                def_total: "\${data.steps.monster_defense.roll.total}"
                hero_dmg: "\${data.steps.hero_damage.value}"
                m_hp: "\${data.steps.monster_hp.hp}"
                verdict: "\${data.steps.outcome.label}"
                m_atk_total: "\${data.steps.monster_attack.roll.total}"
                h_def_total: "\${data.steps.hero_defense.roll.total}"
                m_dmg: "\${data.steps.monster_damage.value}"
              mode: text

          - id: emit_result
            type: emit
            input:
              message: "\${data.steps.outcome.label} \${data.inputs.hero_name} dealt \${data.steps.hero_damage.value} dmg to \${data.monster}"
              level: response

      # ── 3. Tally results ──
      - id: think_tally
        type: emit
        input:
          message: "Tallying battle results…"
          level: thinking

      - id: tally
        type: set
        input:
          total_rounds: "\${len(data.inputs.monsters)}"
          battle_log: "\${json(data.steps.battles)}"

      # ── 4. Ask LLM to narrate the adventure ──
      - id: think_narrate
        type: emit
        input:
          message: "Asking the LLM bard to narrate \${data.inputs.hero_name}'s adventure…"
          level: thinking

      - id: narration
        type: llm.call
        input:
          model: gpt-4
          temperature: 0.8
          prompt: >
            You are a fantasy bard telling epic tales.
            Write a short, dramatic story (max 200 words) about the following dungeon adventure.

            Hero: \${data.inputs.hero_name} the \${data.inputs.hero_class}
            Weapon: \${data.steps.hero_stats.weapon}
            Special ability: \${data.steps.hero_stats.special}
            Difficulty: \${data.inputs.difficulty}

            Battle log:
            \${data.steps.tally.battle_log}

            Make it vivid, mention each monster encounter, the dice rolls outcomes,
            and end with a dramatic conclusion. Use markdown formatting.

      - id: think_done
        type: emit
        input:
          message: "🏆 Adventure complete!"
          level: progress

    outputs:
      hero:
        expr: "\${data.inputs.hero_name}"
        type: string
        description: Hero name
      hero_class:
        expr: "\${data.inputs.hero_class}"
        type: string
        description: Hero class played
      weapon:
        expr: "\${data.steps.hero_stats.weapon}"
        type: string
        description: Weapon used during the adventure
      total_rounds:
        expr: "\${data.steps.tally.total_rounds}"
        type: number
        description: Total combat rounds fought
      battle_results:
        expr: "\${data.steps.battles}"
        type: array
        description: Detailed results per combat round
        items:
          type: object
          properties:
            round_summary:
              type: object
              properties:
                text:
                  type: string
            outcome:
              type: object
              properties:
                verdict:
                  type: string
                  description: "One of: critical_hit, victory, miss, hit"
                label:
                  type: string
                  description: Human-readable outcome label
      narration:
        expr: "\${data.steps.narration.text}"
        type: string
        description: LLM-generated epic tale of the adventure
`

export const EXAMPLE_INPUTS = `hero_name: Aldric
hero_class: knight
difficulty: normal
monsters:
  - Goblin
  - Skeleton
  - Dark Elf
  - Stone Golem
`

