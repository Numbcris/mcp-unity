import * as z from 'zod';
import { Logger } from '../utils/logger.js';
import { McpUnity } from '../unity/mcpUnity.js';
import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
import { McpUnityError, ErrorType } from '../utils/errors.js';
import { CallToolResult } from '@modelcontextprotocol/sdk/types.js';

const resolutionSchema = {
  width: z.number().int().min(64).max(2048).optional().describe('Image width in pixels (default 1024, max 2048)'),
  height: z.number().int().min(64).max(2048).optional().describe('Image height in pixels (default 768, max 2048)')
};

function imageResult(response: any, fallbackMessage: string): CallToolResult {
  return {
    content: [
      {
        type: 'image',
        data: response.data,
        mimeType: response.mimeType || 'image/png'
      },
      {
        type: 'text',
        text: response.message || fallbackMessage
      }
    ]
  };
}

// ============================================================================
// CAPTURE SCENE VIEW TOOL
// ============================================================================

const captureSceneViewToolName = 'capture_scene_view';
const captureSceneViewToolDescription = "Captures a screenshot of the Unity Editor's Scene View (the editing viewport, including gizmos/overlays) and returns it as an image";
const captureSceneViewParamsSchema = z.object(resolutionSchema);

export function registerCaptureSceneViewTool(server: McpServer, mcpUnity: McpUnity, logger: Logger) {
  logger.info(`Registering tool: ${captureSceneViewToolName}`);

  server.tool(
    captureSceneViewToolName,
    captureSceneViewToolDescription,
    captureSceneViewParamsSchema.shape,
    async (params: any) => {
      try {
        logger.info(`Executing tool: ${captureSceneViewToolName}`, params);
        const result = await captureSceneViewHandler(mcpUnity, params);
        logger.info(`Tool execution successful: ${captureSceneViewToolName}`);
        return result;
      } catch (error) {
        logger.error(`Tool execution failed: ${captureSceneViewToolName}`, error);
        throw error;
      }
    }
  );
}

async function captureSceneViewHandler(mcpUnity: McpUnity, params: any): Promise<CallToolResult> {
  const response = await mcpUnity.sendRequest({
    method: captureSceneViewToolName,
    params: {
      width: params.width,
      height: params.height
    }
  });

  if (!response.success) {
    throw new McpUnityError(
      ErrorType.TOOL_EXECUTION,
      response.message || 'Failed to capture Scene View'
    );
  }

  return imageResult(response, 'Scene View captured');
}

// ============================================================================
// CAPTURE GAME VIEW TOOL
// ============================================================================

const captureGameViewToolName = 'capture_game_view';
const captureGameViewToolDescription = "Captures what the main game Camera currently sees (the player's view, without Editor gizmos/overlays) and returns it as an image";
const captureGameViewParamsSchema = z.object(resolutionSchema);

export function registerCaptureGameViewTool(server: McpServer, mcpUnity: McpUnity, logger: Logger) {
  logger.info(`Registering tool: ${captureGameViewToolName}`);

  server.tool(
    captureGameViewToolName,
    captureGameViewToolDescription,
    captureGameViewParamsSchema.shape,
    async (params: any) => {
      try {
        logger.info(`Executing tool: ${captureGameViewToolName}`, params);
        const result = await captureGameViewHandler(mcpUnity, params);
        logger.info(`Tool execution successful: ${captureGameViewToolName}`);
        return result;
      } catch (error) {
        logger.error(`Tool execution failed: ${captureGameViewToolName}`, error);
        throw error;
      }
    }
  );
}

async function captureGameViewHandler(mcpUnity: McpUnity, params: any): Promise<CallToolResult> {
  const response = await mcpUnity.sendRequest({
    method: captureGameViewToolName,
    params: {
      width: params.width,
      height: params.height
    }
  });

  if (!response.success) {
    throw new McpUnityError(
      ErrorType.TOOL_EXECUTION,
      response.message || 'Failed to capture Game View'
    );
  }

  return imageResult(response, 'Game View captured');
}

// ============================================================================
// CAPTURE CAMERA TOOL
// ============================================================================

const captureCameraToolName = 'capture_camera';
const captureCameraToolDescription = 'Captures a screenshot from a specific Camera in the scene, found by GameObject instanceId or hierarchy path';
const captureCameraParamsSchema = z.object({
  instanceId: z.number().optional().describe('The instance ID of the Camera GameObject'),
  objectPath: z.string().optional().describe('The hierarchy path of the Camera GameObject (alternative to instanceId)'),
  ...resolutionSchema
});

export function registerCaptureCameraTool(server: McpServer, mcpUnity: McpUnity, logger: Logger) {
  logger.info(`Registering tool: ${captureCameraToolName}`);

  server.tool(
    captureCameraToolName,
    captureCameraToolDescription,
    captureCameraParamsSchema.shape,
    async (params: any) => {
      try {
        logger.info(`Executing tool: ${captureCameraToolName}`, params);
        const result = await captureCameraHandler(mcpUnity, params);
        logger.info(`Tool execution successful: ${captureCameraToolName}`);
        return result;
      } catch (error) {
        logger.error(`Tool execution failed: ${captureCameraToolName}`, error);
        throw error;
      }
    }
  );
}

async function captureCameraHandler(mcpUnity: McpUnity, params: any): Promise<CallToolResult> {
  if ((params.instanceId === undefined || params.instanceId === null) &&
      (!params.objectPath || params.objectPath.trim() === '')) {
    throw new McpUnityError(
      ErrorType.VALIDATION,
      "Either 'instanceId' or 'objectPath' must be provided"
    );
  }

  const response = await mcpUnity.sendRequest({
    method: captureCameraToolName,
    params: {
      instanceId: params.instanceId,
      objectPath: params.objectPath,
      width: params.width,
      height: params.height
    }
  });

  if (!response.success) {
    throw new McpUnityError(
      ErrorType.TOOL_EXECUTION,
      response.message || 'Failed to capture from camera'
    );
  }

  return imageResult(response, 'Camera view captured');
}

// ============================================================================
// CAPTURE ISOLATED OBJECT TOOL
// ============================================================================

const captureIsolatedObjectToolName = 'capture_isolated_object';
const captureIsolatedObjectToolDescription = 'Captures a GameObject framed automatically from a chosen angle (front, side, top, iso) against a solid background, useful for verifying scale/orientation/appearance without opening the Editor';
const captureIsolatedObjectParamsSchema = z.object({
  instanceId: z.number().optional().describe('The instance ID of the GameObject'),
  objectPath: z.string().optional().describe('The hierarchy path of the GameObject (alternative to instanceId)'),
  angle: z.enum(['front', 'side', 'top', 'iso']).optional().describe('Framing angle (default "iso")'),
  backgroundColor: z.object({
    r: z.number().min(0).max(1),
    g: z.number().min(0).max(1),
    b: z.number().min(0).max(1)
  }).optional().describe('Background color (default a neutral gray)'),
  ...resolutionSchema
});

export function registerCaptureIsolatedObjectTool(server: McpServer, mcpUnity: McpUnity, logger: Logger) {
  logger.info(`Registering tool: ${captureIsolatedObjectToolName}`);

  server.tool(
    captureIsolatedObjectToolName,
    captureIsolatedObjectToolDescription,
    captureIsolatedObjectParamsSchema.shape,
    async (params: any) => {
      try {
        logger.info(`Executing tool: ${captureIsolatedObjectToolName}`, params);
        const result = await captureIsolatedObjectHandler(mcpUnity, params);
        logger.info(`Tool execution successful: ${captureIsolatedObjectToolName}`);
        return result;
      } catch (error) {
        logger.error(`Tool execution failed: ${captureIsolatedObjectToolName}`, error);
        throw error;
      }
    }
  );
}

async function captureIsolatedObjectHandler(mcpUnity: McpUnity, params: any): Promise<CallToolResult> {
  if ((params.instanceId === undefined || params.instanceId === null) &&
      (!params.objectPath || params.objectPath.trim() === '')) {
    throw new McpUnityError(
      ErrorType.VALIDATION,
      "Either 'instanceId' or 'objectPath' must be provided"
    );
  }

  const response = await mcpUnity.sendRequest({
    method: captureIsolatedObjectToolName,
    params: {
      instanceId: params.instanceId,
      objectPath: params.objectPath,
      angle: params.angle,
      backgroundColor: params.backgroundColor,
      width: params.width,
      height: params.height
    }
  });

  if (!response.success) {
    throw new McpUnityError(
      ErrorType.TOOL_EXECUTION,
      response.message || 'Failed to capture isolated object'
    );
  }

  return imageResult(response, 'Isolated object captured');
}
