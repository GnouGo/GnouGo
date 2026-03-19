/// <reference types="vite/client" />

// Déclaration de module pour react-diff-viewer-continued
declare module 'react-diff-viewer-continued' {
  import { ComponentType } from 'react';

  export interface ReactDiffViewerProps {
    oldValue: string;
    newValue: string;
    splitView?: boolean;
    leftTitle?: string;
    rightTitle?: string;
    showDiffOnly?: boolean;
    hideLineNumbers?: boolean;
    compareMethod?: 'lines' | 'words' | 'chars';
    extraLinesSurroundingDiff?: number;
    styles?: Record<string, unknown>;
    useDarkTheme?: boolean;
  }

  const ReactDiffViewer: ComponentType<ReactDiffViewerProps>;
  export default ReactDiffViewer;
}

