// ── ThinkingFeed — displays emit thinking / info / progress / response messages ──

import type { ThinkingMessage } from '../types'

interface Props {
  messages: ThinkingMessage[]
  inline?: boolean
}

export function ThinkingFeed({ messages, inline }: Props) {
  if (messages.length === 0) return null

  return (
    <div className={`thinking-feed${inline ? ' thinking-feed--inline' : ''}`}>
      {messages.map((msg, i) => (
        <div key={i} className={`thinking-msg thinking-msg--${msg.level}`}>
          <span className="thinking-msg__dot" />
          <span className="thinking-msg__text">{msg.message}</span>
        </div>
      ))}
    </div>
  )
}

