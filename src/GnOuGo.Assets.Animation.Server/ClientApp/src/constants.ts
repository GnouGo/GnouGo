export const EXAMPLE_WORKFLOW = `version: 1
name: cheerful-team-demo
entrypoint: main
skill:
  description: Visual-only autonomous simulation
  tags: [animation, demo]

workflows:
  main:
    inputs:
      topic:
        type: string
        required: false
        default: Friendly automation
    steps:
      - id: unpack_request
        type: set
        input:
          topic: "\${data.inputs.topic}"

      - id: team_fanout
        type: parallel
        branches:
          - name: research
            steps:
              - id: call_researcher
                type: workflow.call
                input:
                  ref:
                    kind: local
                    name: researcher
          - name: drafting
            steps:
              - id: write_draft
                type: llm.call
                input:
                  prompt: Draft the result
          - name: checks
            steps:
              - id: inspect_tools
                type: mcp.call
                input:
                  server: demo
                  method: inspect

      - id: review
        type: sequence
        steps:
          - id: ask_human
            type: human.input
            input:
              prompt: Is the package ready?
          - id: polish
            type: template.render
            input:
              template: Final package

      - id: send_result
        type: emit
        input:
          message: Done

  researcher:
    steps:
      - id: collect_notes
        type: mcp.call
        input:
          server: library
          method: search
      - id: summarize_notes
        type: llm.call
        input:
          prompt: Summarize the notes
`

export const EXAMPLE_INPUTS = `{
  "topic": "Build a joyful GnOuGo team animation",
  "items": ["design", "code", "review"]
}`
