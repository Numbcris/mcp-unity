import * as z from 'zod';
import { Logger } from '../utils/logger.js';
import { McpUnity } from '../unity/mcpUnity.js';
import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
import { McpUnityError, ErrorType } from '../utils/errors.js';
import { CallToolResult } from '@modelcontextprotocol/sdk/types.js';

const simulateKeyPressToolName = 'simulate_key_press';
const simulateKeyPressToolDescription = "Simulates a keyboard key press-hold-release entirely inside Unity's own Input System (never touches the OS, never needs window focus). Only has an effect in Play Mode. Use to drive gameplay input (e.g. open a UI panel with its bound key) for self-verification via capture_game_view.";
const simulateKeyPressParamsSchema = z.object({
  key: z.string().describe('Key name, matching the UnityEngine.InputSystem.Key enum (e.g. "V", "C", "Space", "Escape")'),
  holdSeconds: z.number().min(0).optional().describe('How long to hold the key down before releasing (default ~0.15s, a quick tap; a minimum of 0.05s is enforced)')
});

/**
 * Registers the Simulate Key Press tool with the MCP server
 */
export function registerSimulateKeyPressTool(server: McpServer, mcpUnity: McpUnity, logger: Logger) {
  logger.info(`Registering tool: ${simulateKeyPressToolName}`);

  server.tool(
    simulateKeyPressToolName,
    simulateKeyPressToolDescription,
    simulateKeyPressParamsSchema.shape,
    async (params: any) => {
      try {
        logger.info(`Executing tool: ${simulateKeyPressToolName}`, params);
        const result = await simulateKeyPressHandler(mcpUnity, params);
        logger.info(`Tool execution successful: ${simulateKeyPressToolName}`);
        return result;
      } catch (error) {
        logger.error(`Tool execution failed: ${simulateKeyPressToolName}`, error);
        throw error;
      }
    }
  );
}

async function simulateKeyPressHandler(mcpUnity: McpUnity, params: any): Promise<CallToolResult> {
  if (!params.key) {
    throw new McpUnityError(
      ErrorType.VALIDATION,
      "Required parameter 'key' must be provided"
    );
  }

  const response = await mcpUnity.sendRequest({
    method: simulateKeyPressToolName,
    params: {
      key: params.key,
      holdSeconds: params.holdSeconds
    }
  });

  if (!response.success) {
    throw new McpUnityError(
      ErrorType.TOOL_EXECUTION,
      response.message || 'Failed to simulate key press'
    );
  }

  return {
    content: [{
      type: 'text',
      text: response.message || `Simulated key '${params.key}'`
    }]
  };
}
