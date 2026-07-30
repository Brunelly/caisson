import type { ApiResult } from '../../topology/services/api-result';

export interface RackSummary {
  id: string;
  externalKey: string;
  name: string;
}

export type RackCatalogueResult = ApiResult<RackSummary[]>;
