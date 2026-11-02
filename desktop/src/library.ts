import type { ClipGroup } from './types';

export interface GroupNode { group: ClipGroup; children: GroupNode[] }

export function groupTree(groups: ClipGroup[]): GroupNode[] {
  const nodes = new Map(groups.map(group => [group.groupId, { group, children: [] } as GroupNode]));
  const roots: GroupNode[] = [];
  for (const group of groups) {
    const node = nodes.get(group.groupId)!;
    const parent = group.parentGroupId ? nodes.get(group.parentGroupId) : undefined;
    const ancestry = new Set([group.groupId]);
    let ancestor = parent;
    while (ancestor && !ancestry.has(ancestor.group.groupId)) {
      ancestry.add(ancestor.group.groupId);
      ancestor = ancestor.group.parentGroupId ? nodes.get(ancestor.group.parentGroupId) : undefined;
    }
    if (parent && !ancestor) parent.children.push(node);
    else roots.push(node);
  }
  return roots;
}

export function groupPath(groups: ClipGroup[], id: string | null): ClipGroup[] {
  const path: ClipGroup[] = [];
  const visited = new Set<string>();
  let current = groups.find(group => group.groupId === id);
  while (current && !visited.has(current.groupId)) {
    path.unshift(current);
    visited.add(current.groupId);
    current = groups.find(group => group.groupId === current?.parentGroupId);
  }
  return path;
}

export function descendantGroupIds(groups: ClipGroup[], id: string): Set<string> {
  const ids = new Set([id]);
  const children = new Map<string, string[]>();
  for (const group of groups) {
    if (!group.parentGroupId) continue;
    const siblings = children.get(group.parentGroupId) ?? [];
    siblings.push(group.groupId);
    children.set(group.parentGroupId, siblings);
  }
  const queue = [id];
  for (let index = 0; index < queue.length; index++) {
    for (const child of children.get(queue[index]) ?? []) {
      if (!ids.has(child)) { ids.add(child); queue.push(child); }
    }
  }
  return ids;
}

export function groupOptions(groups: ClipGroup[]): { value: string; label: string }[] {
  const options: { value: string; label: string }[] = [];
  function visit(nodes: GroupNode[], ancestors: string[]) {
    for (const node of nodes) {
      const path = [...ancestors, node.group.name];
      options.push({ value: node.group.groupId, label: path.join(' / ') });
      visit(node.children, path);
    }
  }
  visit(groupTree(groups), []);
  return options;
}
