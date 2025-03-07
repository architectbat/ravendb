﻿import React, { HTMLAttributes, ReactNode } from "react";

import classNames from "classnames";
import { LazyLoad } from "./LazyLoad";

import "./LocationDistribution.scss";

interface DistributionItemProps extends HTMLAttributes<HTMLDivElement> {
    loading?: boolean;
}

export function DistributionItem(props: DistributionItemProps) {
    const { loading, children, ...rest } = props;
    return (
        <LazyLoad active={loading ?? false}>
            <div className={classNames("distribution-item")} {...rest}>
                {children}
            </div>
        </LazyLoad>
    );
}

export function DistributionSummary(props: { children: ReactNode }) {
    const { children } = props;
    return <div className="distribution-summary">{children}</div>;
}

export function DistributionLegend(props: { children: ReactNode }) {
    const { children } = props;
    return <div className="distribution-legend">{children}</div>;
}

export function LocationDistribution(props: { children: ReactNode }) {
    return <div className="location-distribution">{props.children}</div>;
}
