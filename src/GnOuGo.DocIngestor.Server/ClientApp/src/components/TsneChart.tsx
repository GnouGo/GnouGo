import { useState, useMemo } from 'react';
import {
  ScatterChart,
  Scatter,
  XAxis,
  YAxis,
  CartesianGrid,
  Tooltip,
  ResponsiveContainer,
  Cell,
  ReferenceDot,
} from 'recharts';
import type { TsneHit } from '../api';

interface Props {
  hits: TsneHit[];
  queryPoint: { x: number; y: number };
  query: string;
}

interface ChartPoint {
  x: number;
  y: number;
  rank: number;
  score: number;
  text: string;
  documentId: string;
  chunkId: string;
}

function toPoints(hits: TsneHit[]): ChartPoint[] {
  return hits.map((h, i) => ({
    x: h.x,
    y: h.y,
    rank: i + 1,
    score: h.score,
    text: h.text,
    documentId: h.documentId,
    chunkId: h.chunkId,
  }));
}

/**
 * Continuous colour ramp from red (low score) through amber, blue, to green (high score).
 * Uses HSL interpolation for a smooth gradient.
 */
function scoreToColor(score: number, minScore: number, maxScore: number): string {
  // Normalise score to [0, 1]
  const range = maxScore - minScore;
  const t = range > 0 ? (score - minScore) / range : 1;

  // Hue: 0° = red, 35° = amber, 220° = blue, 140° = green
  // We interpolate through a custom path for aesthetic:
  //  t=0 → hue 0 (red), t=0.33 → hue 35 (amber), t=0.66 → hue 210 (blue), t=1 → hue 140 (green)
  let hue: number;
  let sat: number;
  let lit: number;

  if (t < 0.33) {
    const u = t / 0.33;
    hue = u * 35;
    sat = 75 + u * 5;
    lit = 50 + u * 5;
  } else if (t < 0.66) {
    const u = (t - 0.33) / 0.33;
    hue = 35 + u * 175; // 35 → 210
    sat = 80 - u * 10;
    lit = 55 + u * 5;
  } else {
    const u = (t - 0.66) / 0.34;
    hue = 210 - u * 70; // 210 → 140
    sat = 70 + u * 10;
    lit = 60 - u * 10;
  }

  return `hsl(${Math.round(hue)}, ${Math.round(sat)}%, ${Math.round(lit)}%)`;
}

/** Generate gradient stops for the legend */
function gradientStops(minScore: number, maxScore: number): Array<{ offset: string; color: string }> {
  const stops: Array<{ offset: string; color: string }> = [];
  for (let i = 0; i <= 10; i++) {
    const t = i / 10;
    const score = minScore + t * (maxScore - minScore);
    stops.push({ offset: `${t * 100}%`, color: scoreToColor(score, minScore, maxScore) });
  }
  return stops;
}

const TRUNCATE = 250;

function CustomTooltip({ active, payload }: { active?: boolean; payload?: Array<{ payload: ChartPoint }> }) {
  if (!active || !payload?.length) return null;
  const pt = payload[0].payload;
  const preview = pt.text.length > TRUNCATE ? pt.text.slice(0, TRUNCATE) + '…' : pt.text;

  return (
    <div className="proximity-tooltip">
      <div className="proximity-tooltip__header">
        <span className="proximity-tooltip__rank">#{pt.rank}</span>
        <span className="proximity-tooltip__score">{(pt.score * 100).toFixed(1)}%</span>
        <span className="proximity-tooltip__doc">{pt.documentId}</span>
      </div>
      <p className="proximity-tooltip__text">{preview}</p>
    </div>
  );
}

export function TsneChart({ hits, queryPoint, query }: Props) {
  const [highlighted, setHighlighted] = useState<number | null>(null);

  const points = useMemo(() => toPoints(hits), [hits]);

  const { minScore, maxScore } = useMemo(() => {
    if (points.length === 0) return { minScore: 0, maxScore: 1 };
    const scores = points.map(p => p.score);
    return { minScore: Math.min(...scores), maxScore: Math.max(...scores) };
  }, [points]);

  const stops = useMemo(() => gradientStops(minScore, maxScore), [minScore, maxScore]);

  // Compute symmetrical domain so the chart is centered
  const maxR = useMemo(() => {
    if (points.length === 0) return 1;
    const allX = [...points.map(p => Math.abs(p.x)), Math.abs(queryPoint.x)];
    const allY = [...points.map(p => Math.abs(p.y)), Math.abs(queryPoint.y)];
    const m = Math.max(...allX, ...allY);
    return Math.ceil(m * 1.2 * 10) / 10; // 20% padding
  }, [points, queryPoint]);

  if (hits.length === 0) return null;

  const gradientId = 'tsne-score-gradient';

  return (
    <div className="tsne-chart">
      <h3 className="tsne-chart__title">t-SNE Embedding Map</h3>
      <p className="tsne-chart__subtitle">
        Query: <em>"{query}"</em> — points positioned by t-SNE projection of high-dimensional embeddings
      </p>

      {/* Hidden SVG for gradient definition */}
      <svg width={0} height={0} style={{ position: 'absolute' }}>
        <defs>
          <linearGradient id={gradientId} x1="0%" y1="0%" x2="100%" y2="0%">
            {stops.map((s, i) => (
              <stop key={i} offset={s.offset} stopColor={s.color} />
            ))}
          </linearGradient>
        </defs>
      </svg>

      <ResponsiveContainer width="100%" height={480}>
        <ScatterChart margin={{ top: 20, right: 20, bottom: 20, left: 20 }}>
          <CartesianGrid strokeDasharray="3 3" stroke="#2d3148" />
          <XAxis
            type="number"
            dataKey="x"
            domain={[-maxR, maxR]}
            tick={{ fill: '#8b90a5', fontSize: 11 }}
            axisLine={{ stroke: '#2d3148' }}
            tickLine={false}
            name="t-SNE 1"
            hide
          />
          <YAxis
            type="number"
            dataKey="y"
            domain={[-maxR, maxR]}
            tick={{ fill: '#8b90a5', fontSize: 11 }}
            axisLine={{ stroke: '#2d3148' }}
            tickLine={false}
            name="t-SNE 2"
            hide
          />

          <Tooltip
            content={<CustomTooltip />}
            cursor={false}
            wrapperStyle={{ zIndex: 100 }}
          />

          {/* Query reference point */}
          <ReferenceDot
            x={queryPoint.x}
            y={queryPoint.y}
            r={10}
            fill="#fff"
            stroke="#6c8cff"
            strokeWidth={3}
          />

          {/* Result points with score-based gradient colors */}
          <Scatter
            data={points}
            onMouseEnter={(_, idx) => setHighlighted(idx)}
            onMouseLeave={() => setHighlighted(null)}
          >
            {points.map((pt, idx) => (
              <Cell
                key={pt.chunkId}
                fill={scoreToColor(pt.score, minScore, maxScore)}
                r={highlighted === idx ? 11 : 7}
                stroke={highlighted === idx ? '#fff' : 'rgba(255,255,255,0.15)'}
                strokeWidth={highlighted === idx ? 2 : 1}
                style={{ cursor: 'pointer', transition: 'r 0.15s' }}
              />
            ))}
          </Scatter>
        </ScatterChart>
      </ResponsiveContainer>

      {/* Gradient legend */}
      <div className="tsne-chart__legend">
        <span className="tsne-chart__legend-item">
          <span className="tsne-chart__dot" style={{ background: '#fff', border: '2px solid #6c8cff' }} /> Query
        </span>
        <div className="tsne-chart__gradient-legend">
          <span className="tsne-chart__gradient-label">{(minScore * 100).toFixed(0)}%</span>
          <div className="tsne-chart__gradient-bar" style={{ background: `linear-gradient(to right, ${stops.map(s => s.color).join(', ')})` }} />
          <span className="tsne-chart__gradient-label">{(maxScore * 100).toFixed(0)}%</span>
        </div>
      </div>
    </div>
  );
}
