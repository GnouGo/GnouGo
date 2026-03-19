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
import type { SearchHit } from '../types';

interface Props {
  hits: SearchHit[];
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

/**
 * Convert similarity scores to 2D Euclidean coordinates.
 * 
 * Strategy: the query sits at the origin (0,0).
 * Each result is placed at distance = 1 − score (high similarity → close to center).
 * The angle is distributed based on rank with a golden-angle spread to avoid overlaps,
 * giving a visually pleasing distribution.
 */
function toPoints(hits: SearchHit[]): ChartPoint[] {
  const goldenAngle = Math.PI * (3 - Math.sqrt(5)); // ≈ 2.399 radians

  return hits.map((h, i) => {
    const distance = 1 - h.score;                    // 0 → identical, 1 → orthogonal
    const angle = i * goldenAngle;                    // golden-angle spread
    return {
      x: Math.round(distance * Math.cos(angle) * 1000) / 1000,
      y: Math.round(distance * Math.sin(angle) * 1000) / 1000,
      rank: i + 1,
      score: h.score,
      text: h.text,
      documentId: h.documentId,
      chunkId: h.chunkId,
    };
  });
}

function scoreToColor(score: number): string {
  // High score → green, low → amber/red
  if (score >= 0.85) return '#4caf77';
  if (score >= 0.70) return '#6c8cff';
  if (score >= 0.50) return '#f0a030';
  return '#e05555';
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

export function ProximityChart({ hits, query }: Props) {
  const [highlighted, setHighlighted] = useState<number | null>(null);

  const points = useMemo(() => toPoints(hits), [hits]);

  // Compute symmetrical domain so the query is centered
  const maxR = useMemo(() => {
    if (points.length === 0) return 0.5;
    const m = Math.max(...points.map(p => Math.max(Math.abs(p.x), Math.abs(p.y))));
    return Math.ceil(m * 12) / 10; // small padding
  }, [points]);

  if (hits.length === 0) return null;

  return (
    <div className="proximity-chart">
      <h3 className="proximity-chart__title">Semantic Proximity Map</h3>
      <p className="proximity-chart__subtitle">
        Query: <em>"{query}"</em> — closer to center = higher similarity
      </p>

      <ResponsiveContainer width="100%" height={420}>
        <ScatterChart margin={{ top: 20, right: 20, bottom: 20, left: 20 }}>
          <CartesianGrid strokeDasharray="3 3" stroke="#2d3148" />
          <XAxis
            type="number"
            dataKey="x"
            domain={[-maxR, maxR]}
            tick={{ fill: '#8b90a5', fontSize: 11 }}
            axisLine={{ stroke: '#2d3148' }}
            tickLine={false}
            name="X"
            hide
          />
          <YAxis
            type="number"
            dataKey="y"
            domain={[-maxR, maxR]}
            tick={{ fill: '#8b90a5', fontSize: 11 }}
            axisLine={{ stroke: '#2d3148' }}
            tickLine={false}
            name="Y"
            hide
          />

          <Tooltip
            content={<CustomTooltip />}
            cursor={false}
            wrapperStyle={{ zIndex: 100 }}
          />

          {/* Query reference point at origin */}
          <ReferenceDot
            x={0}
            y={0}
            r={8}
            fill="#fff"
            stroke="#6c8cff"
            strokeWidth={3}
          />

          {/* Result points */}
          <Scatter
            data={points}
            onMouseEnter={(_, idx) => setHighlighted(idx)}
            onMouseLeave={() => setHighlighted(null)}
          >
            {points.map((pt, idx) => (
              <Cell
                key={pt.chunkId}
                fill={scoreToColor(pt.score)}
                r={highlighted === idx ? 10 : 6}
                stroke={highlighted === idx ? '#fff' : 'none'}
                strokeWidth={highlighted === idx ? 2 : 0}
                style={{ cursor: 'pointer', transition: 'r 0.15s' }}
              />
            ))}
          </Scatter>
        </ScatterChart>
      </ResponsiveContainer>

      {/* Legend */}
      <div className="proximity-chart__legend">
        <span className="proximity-chart__legend-item">
          <span className="proximity-chart__dot" style={{ background: '#fff', border: '2px solid #6c8cff' }} /> Query
        </span>
        <span className="proximity-chart__legend-item">
          <span className="proximity-chart__dot" style={{ background: '#4caf77' }} /> ≥ 85%
        </span>
        <span className="proximity-chart__legend-item">
          <span className="proximity-chart__dot" style={{ background: '#6c8cff' }} /> ≥ 70%
        </span>
        <span className="proximity-chart__legend-item">
          <span className="proximity-chart__dot" style={{ background: '#f0a030' }} /> ≥ 50%
        </span>
        <span className="proximity-chart__legend-item">
          <span className="proximity-chart__dot" style={{ background: '#e05555' }} /> &lt; 50%
        </span>
      </div>
    </div>
  );
}

